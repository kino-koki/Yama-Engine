// ============================================================================
//  YamaEngine.AI.FiniteStateMachineSystem.cs
//  yama引擎 -- 有限状态机系统 (Finite State Machine System)
//  通用泛型状态机，支持：
//    - 基本状态机
//    - 并行状态机
//    - 层级状态机（嵌套/状态内状态机）
//    - 历史状态
// ============================================================================
//  完整的有限状态机系统，包含：
//    1. State<T>                  - 状态类 (进入/更新/退出/条件转换)
//    2. Transition<T>             - 转换类 (源/目标/条件/优先级/一次性)
//    3. TransitionCondition<T>   - 转换条件 (Evaluate -> bool)
//    4. StateMachine<T>           - 泛型状态机
//    5. ParallelStateMachine<T>   - 并行状态机 (多个独立状态机同时运行)
//    6. HierarchicalStateMachine<T> - 层级状态机 (嵌套状态机/状态内状态机)
//    7. 历史状态 (记住上次处于的子状态)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、转换条件 (TransitionCondition<T>)
    // ========================================================================

    /// <summary>
    /// 转换条件委托。
    /// </summary>
    public delegate bool TransitionCondition<T>(T context);

    // ========================================================================
    //  二、转换 (Transition<T>)
    // ========================================================================

    /// <summary>
    /// 状态转换。描述从源状态到目标状态的条件驱动的切换。
    /// </summary>
    public sealed class Transition<T>
    {
        /// <summary>源状态名称。</summary>
        public string FromState { get; set; }
        /// <summary>目标状态名称。</summary>
        public string ToState { get; set; }
        /// <summary>转换条件。</summary>
        public TransitionCondition<T> Condition { get; set; }
        /// <summary>优先级（数值越小优先级越高）。</summary>
        public int Priority { get; set; } = 0;
        /// <summary>是否一次性（触发后自动删除）。</summary>
        public bool IsOneShot { get; set; } = false;
        /// <summary>是否已触发（用于一次性转换）。</summary>
        internal bool _triggered = false;
        /// <summary>转换时回调。</summary>
        public Action<T> OnTransition { get; set; }

        /// <summary>转换名称（调试用）。</summary>
        public string TransitionName { get; set; } = "";

        public Transition() { }

        public Transition(string from, string to, TransitionCondition<T> condition, int priority = 0)
        {
            FromState = from;
            ToState = to;
            Condition = condition;
            Priority = priority;
            TransitionName = $"{from} -> {to}";
        }

        /// <summary>评估转换条件。</summary>
        public bool Evaluate(T context)
        {
            if (IsOneShot && _triggered) return false;
            if (Condition != null && Condition(context))
            {
                if (IsOneShot) _triggered = true;
                return true;
            }
            return false;
        }
    }

    // ========================================================================
    //  三、状态 (State<T>)
    // ========================================================================

    /// <summary>
    /// 状态类。描述一种状态的进入/更新/退出逻辑和内嵌转换。
    /// </summary>
    public sealed class State<T>
    {
        /// <summary>状态名称。</summary>
        public string StateName { get; set; } = "";
        /// <summary>状态描述。</summary>
        public string Description { get; set; } = "";
        /// <summary>状态标签。</summary>
        public HashSet<string> Tags { get; } = new HashSet<string>();

        /// <summary>进入状态回调。</summary>
        public Action<T> OnEnter { get; set; }
        /// <summary>更新状态回调（每帧）。</summary>
        public Action<T, float> OnUpdate { get; set; }
        /// <summary>退出状态回调。</summary>
        public Action<T> OnExit { get; set; }

        /// <summary>内部转换列表。</summary>
        public List<Transition<T>> Transitions { get; } = new List<Transition<T>>();

        /// <summary>最小持续时间。</summary>
        public float MinDuration { get; set; } = 0f;
        /// <summary>最大持续时间（0=无限）。</summary>
        public float MaxDuration { get; set; } = 0f;

        /// <summary>状态已经历的时间。</summary>
        public float ElapsedTime { get; private set; }
        private float _duration = 0f;

        /// <summary>内嵌的子状态机（用于层级状态机）。</summary>
        public StateMachine<T> SubStateMachine { get; set; }

        public State() { }

        public State(string name)
        {
            StateName = name;
        }

        /// <summary>进入状态。</summary>
        public void Enter(T context)
        {
            ElapsedTime = 0f;
            if (MaxDuration > 0f)
            {
                var rng = new Random();
                _duration = MinDuration + (float)rng.NextDouble() * (MaxDuration - MinDuration);
            }
            OnEnter?.Invoke(context);
            SubStateMachine?.Start(context);
        }

        /// <summary>更新状态。</summary>
        public void Update(T context, float deltaTime)
        {
            ElapsedTime += deltaTime;
            OnUpdate?.Invoke(context, deltaTime);
            SubStateMachine?.Update(context, deltaTime);
        }

        /// <summary>退出状态。</summary>
        public void Exit(T context)
        {
            SubStateMachine?.Stop(context);
            OnExit?.Invoke(context);
        }

        /// <summary>持续时间是否已过期。</summary>
        public bool IsDurationExpired() => MaxDuration > 0f && ElapsedTime >= _duration;

        /// <summary>添加内嵌转换。</summary>
        public Transition<T> AddTransition(Transition<T> transition)
        {
            Transitions.Add(transition);
            return transition;
        }

        /// <summary>创建并添加一个转换。</summary>
        public Transition<T> AddTransition(string targetState, TransitionCondition<T> condition,
            int priority = 0)
        {
            var transition = new Transition<T>(StateName, targetState, condition, priority);
            Transitions.Add(transition);
            return transition;
        }
    }

    // ========================================================================
    //  四、状态机 (StateMachine<T>)
    // ========================================================================

    /// <summary>
    /// 泛型有限状态机。支持状态、转换、历史状态。
    /// </summary>
    public sealed class StateMachine<T>
    {
        /// <summary>所有状态。</summary>
        private readonly Dictionary<string, State<T>> _states =
            new Dictionary<string, State<T>>();

        /// <summary>所有转换。</summary>
        private readonly List<Transition<T>> _transitions =
            new List<Transition<T>>();

        /// <summary>当前状态。</summary>
        private State<T> _currentState;
        /// <summary>初始状态名称。</summary>
        public string InitialState { get; set; } = "";

        /// <summary>历史状态记录。</summary>
        private readonly Dictionary<string, string> _historyStates =
            new Dictionary<string, string>();

        /// <summary>是否启用历史状态。</summary>
        public bool EnableHistory { get; set; } = false;

        /// <summary>当前状态名称。</summary>
        public string CurrentStateName => _currentState?.StateName ?? "";

        /// <summary>当前状态。</summary>
        public State<T> CurrentState => _currentState;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>状态变更回调。</summary>
        public Action<string, string, T> OnStateChanged { get; set; }

        /// <summary>转换回调（转换发生时）。</summary>
        public Action<Transition<T>, T> OnTransition { get; set; }

        public StateMachine() { }

        /// <summary>添加状态。</summary>
        public State<T> AddState(State<T> state)
        {
            if (state == null) return null;
            _states[state.StateName] = state;
            if (string.IsNullOrEmpty(InitialState))
                InitialState = state.StateName;
            return state;
        }

        /// <summary>创建并添加状态。</summary>
        public State<T> AddState(string name)
        {
            var state = new State<T>(name);
            return AddState(state);
        }

        /// <summary>添加转换。</summary>
        public Transition<T> AddTransition(Transition<T> transition)
        {
            if (transition != null)
                _transitions.Add(transition);
            return transition;
        }

        /// <summary>创建并添加转换。</summary>
        public Transition<T> AddTransition(string from, string to, TransitionCondition<T> condition,
            int priority = 0, bool isOneShot = false)
        {
            var transition = new Transition<T>(from, to, condition, priority)
            {
                IsOneShot = isOneShot,
                TransitionName = $"{from} -> {to}"
            };
            _transitions.Add(transition);
            return transition;
        }

        /// <summary>添加状态内转换（简写方法）。</summary>
        public Transition<T> From(string stateName)
        {
            var transition = new Transition<T> { FromState = stateName };
            _transitions.Add(transition);
            return transition;
        }

        /// <summary>获取状态。</summary>
        public State<T> GetState(string name)
        {
            _states.TryGetValue(name, out var state);
            return state;
        }

        /// <summary>获取所有状态名称。</summary>
        public List<string> GetStateNames()
        {
            return new List<string>(_states.Keys);
        }

        /// <summary>启动状态机。</summary>
        public void Start(T context)
        {
            IsRunning = true;
            if (!string.IsNullOrEmpty(InitialState) && _currentState == null)
            {
                ForceState(InitialState, context);
            }
        }

        /// <summary>停止状态机。</summary>
        public void Stop(T context)
        {
            if (_currentState != null)
            {
                _currentState.Exit(context);
                if (EnableHistory)
                    _historyStates[_currentState.StateName] = _currentState.StateName;
            }
            _currentState = null;
            IsRunning = false;
        }

        /// <summary>每帧更新。</summary>
        public void Update(T context, float deltaTime)
        {
            if (!IsRunning || _currentState == null) return;

            // 1. 检查全局转换
            var globalTransition = FindBestTransition(null, context);
            if (globalTransition != null)
            {
                PerformTransition(globalTransition, context);
                return;
            }

            // 2. 检查状态内转换
            var internalTransition = FindBestTransition(_currentState.StateName, context);
            if (internalTransition != null)
            {
                PerformTransition(internalTransition, context);
                return;
            }

            // 3. 检查状态内嵌子状态机的转换
            if (_currentState.SubStateMachine != null)
            {
                var subTransition = _currentState.SubStateMachine.FindBestTransition(null, context);
                if (subTransition != null)
                {
                    _currentState.SubStateMachine.PerformTransition(subTransition, context);
                    return;
                }
            }

            // 4. 检查持续时间
            if (_currentState.IsDurationExpired())
            {
                ForceState(InitialState, context);
                return;
            }

            // 5. 更新当前状态
            _currentState.Update(context, deltaTime);
        }

        /// <summary>强制切换到指定状态。</summary>
        public void ForceState(string stateName, T context)
        {
            if (!_states.TryGetValue(stateName, out var newState)) return;

            var prevState = _currentState?.StateName ?? "";
            _currentState?.Exit(context);

            // 记录历史
            if (EnableHistory && _currentState != null)
                _historyStates[_currentState.StateName] = _currentState.StateName;

            _currentState = newState;
            _currentState.Enter(context);
            OnStateChanged?.Invoke(prevState, stateName, context);
        }

        /// <summary>切换到历史状态。</summary>
        public void GoToHistoryState(string sourceState, T context)
        {
            if (_historyStates.TryGetValue(sourceState, out var historyState))
                ForceState(historyState, context);
            else if (!string.IsNullOrEmpty(InitialState))
                ForceState(InitialState, context);
        }

        /// <summary>查找最佳转换。</summary>
        internal Transition<T> FindBestTransition(string currentStateName, T context)
        {
            Transition<T> best = null;
            int bestPriority = int.MaxValue;

            foreach (var transition in _transitions)
            {
                // 检查源状态匹配
                if (!string.IsNullOrEmpty(transition.FromState) &&
                    transition.FromState != currentStateName)
                    continue;

                // 全局转换（FromState为空）适用于任何状态
                if (transition.FromState != null && currentStateName == null)
                    continue;

                if (transition.Evaluate(context))
                {
                    if (transition.Priority < bestPriority)
                    {
                        bestPriority = transition.Priority;
                        best = transition;
                    }
                }
            }
            return best;
        }

        /// <summary>执行转换。</summary>
        internal void PerformTransition(Transition<T> transition, T context)
        {
            transition.OnTransition?.Invoke(context);
            OnTransition?.Invoke(transition, context);
            ForceState(transition.ToState, context);
        }

        /// <summary>清除所有一次性转换的触发状态。</summary>
        public void ResetOneShotTransitions()
        {
            foreach (var t in _transitions)
                t._triggered = false;
        }

        /// <summary>序列化当前状态。</summary>
        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object>
            {
                { "currentState", CurrentStateName },
                { "historyStates", new Dictionary<string, string>(_historyStates) }
            };
        }
    }

    // ========================================================================
    //  五、并行状态机 (ParallelStateMachine<T>)
    // ========================================================================

    /// <summary>
    /// 并行状态机。多个独立状态机同时运行。
    /// </summary>
    public sealed class ParallelStateMachine<T>
    {
        private readonly List<(StateMachine<T> Machine, string Name)> _machines =
            new List<(StateMachine<T>, string)>();

        /// <summary>是否所有机器都在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>状态变更回调。</summary>
        public Action<string, string, string, T> OnChildStateChanged { get; set; }

        /// <summary>添加子状态机。</summary>
        public void AddMachine(string name, StateMachine<T> machine)
        {
            machine.OnStateChanged += (from, to, ctx) =>
            {
                OnChildStateChanged?.Invoke(name, from, to, ctx);
            };
            _machines.Add((machine, name));
        }

        /// <summary>移除子状态机。</summary>
        public void RemoveMachine(string name)
        {
            _machines.RemoveAll(m => m.Name == name);
        }

        /// <summary>获取子状态机。</summary>
        public StateMachine<T> GetMachine(string name)
        {
            foreach (var m in _machines)
                if (m.Name == name) return m.Machine;
            return null;
        }

        /// <summary>启动所有子状态机。</summary>
        public void Start(T context)
        {
            IsRunning = true;
            foreach (var (machine, _) in _machines)
                machine.Start(context);
        }

        /// <summary>停止所有子状态机。</summary>
        public void Stop(T context)
        {
            foreach (var (machine, _) in _machines)
                machine.Stop(context);
            IsRunning = false;
        }

        /// <summary>更新所有子状态机。</summary>
        public void Update(T context, float deltaTime)
        {
            if (!IsRunning) return;
            foreach (var (machine, _) in _machines)
                machine.Update(context, deltaTime);
        }

        /// <summary>获取所有子状态机的当前状态。</summary>
        public Dictionary<string, string> GetAllCurrentStates()
        {
            var result = new Dictionary<string, string>();
            foreach (var (machine, name) in _machines)
                result[name] = machine.CurrentStateName;
            return result;
        }
    }

    // ========================================================================
    //  六、层级状态机 (HierarchicalStateMachine<T>)
    // ========================================================================

    /// <summary>
    /// 层级状态机。支持嵌套状态机（状态内状态机）和历史状态。
    /// </summary>
    public class HierarchicalStateMachine<T>
    {
        /// <summary>根状态机。</summary>
        public StateMachine<T> RootStateMachine { get; protected set; } = new StateMachine<T>();

        /// <summary>上下文类型。</summary>
        private T _context;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => RootStateMachine.IsRunning;

        /// <summary>当前根状态名称。</summary>
        public string CurrentRootStateName => RootStateMachine.CurrentStateName;

        /// <summary>全局状态变更回调。</summary>
        public Action<string, string, string> OnStateChanged { get; set; }

        public HierarchicalStateMachine() { }

        /// <summary>添加顶层状态。</summary>
        public State<T> AddRootState(State<T> state)
        {
            return RootStateMachine.AddState(state);
        }

        /// <summary>创建并添加顶层状态。</summary>
        public State<T> AddRootState(string name)
        {
            return RootStateMachine.AddState(name);
        }

        /// <summary>添加顶层转换。</summary>
        public Transition<T> AddRootTransition(Transition<T> transition)
        {
            return RootStateMachine.AddTransition(transition);
        }

        /// <summary>添加顶层转换（简写）。</summary>
        public Transition<T> AddRootTransition(string from, string to,
            TransitionCondition<T> condition, int priority = 0)
        {
            return RootStateMachine.AddTransition(from, to, condition, priority);
        }

        /// <summary>为指定状态设置子状态机。</summary>
        public StateMachine<T> SetSubStateMachine(string parentStateName,
            string initialSubState, Action<HierarchicalStateMachine<T>> configureSub)
        {
            var parentState = RootStateMachine.GetState(parentStateName);
            if (parentState == null) return null;

            var subMachine = new StateMachine<T> { InitialState = initialSubState };
            parentState.SubStateMachine = subMachine;

            if (configureSub != null)
            {
                // 创建一个临时的层级包装来配置子状态机
                var tempHierarchical = new TempHierarchicalWrapper(subMachine);
                configureSub(tempHierarchical);
            }

            return subMachine;
        }

        /// <summary>启动。</summary>
        public void Start(T context)
        {
            _context = context;
            RootStateMachine.OnStateChanged += (from, to, ctx) =>
            {
                OnStateChanged?.Invoke(from, to, null);
            };
            RootStateMachine.Start(context);
        }

        /// <summary>停止。</summary>
        public void Stop()
        {
            RootStateMachine.Stop(_context);
        }

        /// <summary>更新。</summary>
        public void Update(float deltaTime)
        {
            RootStateMachine.Update(_context, deltaTime);
        }

        /// <summary>强制切换根状态。</summary>
        public void ForceRootState(string stateName)
        {
            RootStateMachine.ForceState(stateName, _context);
        }

        /// <summary>获取当前完整的状态路径（如 "Combat.Attack"）。</summary>
        public string GetCurrentStatePath()
        {
            var current = RootStateMachine.CurrentState;
            if (current == null) return "";

            string path = current.StateName;
            while (current.SubStateMachine != null)
            {
                current = current.SubStateMachine.CurrentState;
                if (current == null) break;
                path += "." + current.StateName;
            }
            return path;
        }

        /// <summary>获取当前状态层级。</summary>
        public List<State<T>> GetCurrentStateHierarchy()
        {
            var hierarchy = new List<State<T>>();
            var current = RootStateMachine.CurrentState;
            if (current != null)
            {
                hierarchy.Add(current);
                while (current.SubStateMachine != null)
                {
                    current = current.SubStateMachine.CurrentState;
                    if (current == null) break;
                    hierarchy.Add(current);
                }
            }
            return hierarchy;
        }

        /// <summary>获取指定状态路径对应的状态。</summary>
        public State<T> GetStateAtPath(string path)
        {
            var parts = path.Split('.');
            var current = RootStateMachine.GetState(parts[0]);
            if (current == null || parts.Length == 1) return current;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.SubStateMachine?.GetState(parts[i]);
                if (current == null) return null;
            }
            return current;
        }

        // ── 内部辅助类 ──────────────────────────────────────────

        /// <summary>
        /// 临时层级包装器，用于配置子状态机。
        /// </summary>
        private class TempHierarchicalWrapper : HierarchicalStateMachine<T>
        {
            public TempHierarchicalWrapper(StateMachine<T> machine)
            {
                RootStateMachine = machine;
            }
        }
    }

    // ========================================================================
    //  七、便捷状态机工厂
    // ========================================================================

    /// <summary>
    /// 状态机构建器。提供链式 API 快速创建状态机。
    /// </summary>
    public sealed class StateMachineBuilder<T>
    {
        private readonly StateMachine<T> _machine = new StateMachine<T>();

        public StateMachineBuilder<T> SetInitial(string stateName)
        {
            _machine.InitialState = stateName;
            return this;
        }

        public StateMachineBuilder<T> EnableHistory(bool enable = true)
        {
            _machine.EnableHistory = enable;
            return this;
        }

        public StateMachineBuilder<T> State(string name, Action<State<T>> configure = null)
        {
            var state = new State<T>(name);
            configure?.Invoke(state);
            _machine.AddState(state);
            return this;
        }

        public StateMachineBuilder<T> Transition(string from, string to,
            TransitionCondition<T> condition, int priority = 0, bool isOneShot = false)
        {
            _machine.AddTransition(from, to, condition, priority, isOneShot);
            return this;
        }

        public StateMachineBuilder<T> OnStateChanged(Action<string, string, T> callback)
        {
            _machine.OnStateChanged += callback;
            return this;
        }

        public StateMachine<T> Build()
        {
            return _machine;
        }
    }
}
