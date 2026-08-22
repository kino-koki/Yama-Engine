using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace ShanyuEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 · 状态机图可视化系统 (State Machine Graph System)
    //  ────────────────────────────────────────────────────────────────────────────
    //  实现可视化状态机编辑器和运行时系统：
    //    · StateGraphNode         — 状态节点（名称/进入/更新/退出/输出引脚/注释）
    //    · TransitionGraphNode    — 转换节点（条件/优先级/一次性/注释）
    //    · StateMachineGraph      — 状态机图（状态/转换/初始状态/任意状态）
    //    · StateMachineInstance   — 运行时状态机实例
    //    · StateMachineDebugger   — 状态机调试器（当前状态高亮/转换历史/条件值）
    //  作者：山雨引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  StateGraphNode - 状态节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机状态节点：表示状态机中的一个状态。
    /// <para>包含状态名、进入/更新/退出行为节点图、输出转换引脚和注释。</para>
    /// </summary>
    [Serializable]
    public class StateGraphNode
    {
        /// <summary>状态节点唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>状态名称。</summary>
        public string StateName { get; set; } = "NewState";

        /// <summary>状态显示标题。</summary>
        public string DisplayName { get; set; } = "New State";

        /// <summary>状态描述。</summary>
        public string? Description { get; set; }

        /// <summary>节点在画布上的位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小。</summary>
        public Vector2 Size { get; set; } = new Vector2(200, 120);

        /// <summary>节点颜色（十六进制字符串）。</summary>
        public string ColorHex { get; set; } = "#4488CC";

        /// <summary>是否为初始状态。</summary>
        public bool IsInitialState { get; set; }

        /// <summary>是否为任意状态（Any State，可从任何状态转换）。</summary>
        public bool IsAnyState { get; set; }

        /// <summary>是否被折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>状态注释。</summary>
        public string? Comment { get; set; }

        /// <summary>输出转换引脚列表（每个引脚代表一个可能的转换出口）。</summary>
        public List<StateTransitionPin> TransitionPins { get; set; } = new List<StateTransitionPin>();

        /// <summary>进入状态时执行的节点图。</summary>
        public NodeGraph? OnEnterGraph { get; set; }

        /// <summary>状态更新时每帧执行的节点图。</summary>
        public NodeGraph? OnUpdateGraph { get; set; }

        /// <summary>退出状态时执行的节点图。</summary>
        public NodeGraph? OnExitGraph { get; set; }

        /// <summary>进入状态的执行引脚数量（用于编辑器显示）。</summary>
        [JsonIgnore]
        public int EnterPinCount => OnEnterGraph != null ? 1 : 0;

        /// <summary>更新引脚数量。</summary>
        [JsonIgnore]
        public int UpdatePinCount => OnUpdateGraph != null ? 1 : 0;

        /// <summary>退出引脚数量。</summary>
        [JsonIgnore]
        public int ExitPinCount => OnExitGraph != null ? 1 : 0;

        /// <summary>
        /// 添加一个输出转换引脚。
        /// </summary>
        public StateTransitionPin AddTransitionPin(string name = "Transition")
        {
            var pin = new StateTransitionPin
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerStateId = Id,
                OrderIndex = TransitionPins.Count
            };
            TransitionPins.Add(pin);
            return pin;
        }

        /// <summary>
        /// 移除输出转换引脚。
        /// </summary>
        public bool RemoveTransitionPin(Guid pinId)
        {
            return TransitionPins.RemoveAll(p => p.Id == pinId) > 0;
        }

        /// <summary>
        /// 通过 ID 获取转换引脚。
        /// </summary>
        public StateTransitionPin? GetTransitionPin(Guid pinId)
        {
            return TransitionPins.FirstOrDefault(p => p.Id == pinId);
        }

        /// <summary>
        /// 检查一个点是否在节点内。
        /// </summary>
        public bool ContainsPoint(Vector2 point)
        {
            return point.X >= Position.X && point.X <= Position.X + Size.X &&
                   point.Y >= Position.Y && point.Y <= Position.Y + Size.Y;
        }

        /// <summary>
        /// 创建一个新的状态节点。
        /// </summary>
        public static StateGraphNode Create(string stateName, Vector2 position = default, bool isInitial = false)
        {
            return new StateGraphNode
            {
                StateName = stateName,
                DisplayName = stateName,
                Position = position,
                IsInitialState = isInitial,
                ColorHex = isInitial ? "#00AA44" : "#4488CC",
                Size = new Vector2(200, 120)
            };
        }

        /// <summary>
        /// 序列化为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化。
        /// </summary>
        public static StateGraphNode? FromJson(string json)
        {
            return JsonSerializer.Deserialize<StateGraphNode>(json);
        }
    }

    /// <summary>
    /// 状态转换引脚：状态节点上的输出转换端口。
    /// </summary>
    [Serializable]
    public class StateTransitionPin
    {
        /// <summary>引脚唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>所属状态节点 ID。</summary>
        public Guid OwnerStateId { get; set; }

        /// <summary>引脚名称。</summary>
        public string Name { get; set; } = "Transition";

        /// <summary>引脚在节点上的显示顺序。</summary>
        public int OrderIndex { get; set; }

        /// <summary>引脚在节点上的 Y 偏移量。</summary>
        public float YOffset { get; set; }

        /// <summary>该引脚的连接列表。</summary>
        public List<Guid> TransitionIds { get; set; } = new List<Guid>();

        /// <summary>是否被连接。</summary>
        [JsonIgnore]
        public bool IsConnected => TransitionIds.Count > 0;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  TransitionGraphNode - 转换节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态转换节点：定义从一个状态到另一个状态的转换条件和规则。
    /// </summary>
    [Serializable]
    public class TransitionGraphNode
    {
        /// <summary>转换唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>转换名称。</summary>
        public string TransitionName { get; set; } = "Transition";

        /// <summary>转换描述。</summary>
        public string? Description { get; set; }

        /// <summary>节点在画布上的位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小。</summary>
        public Vector2 Size { get; set; } = new Vector2(180, 80);

        /// <summary>节点颜色。</summary>
        public string ColorHex { get; set; } = "#666666";

        /// <summary>源状态节点 ID。</summary>
        public Guid SourceStateId { get; set; }

        /// <summary>源转换引脚 ID。</summary>
        public Guid SourcePinId { get; set; }

        /// <summary>目标状态节点 ID。</summary>
        public Guid TargetStateId { get; set; }

        /// <summary>转换优先级（数字越小优先级越高）。</summary>
        public int Priority { get; set; }

        /// <summary>是否为一次性转换（触发后自动禁用）。</summary>
        public bool IsOneShot { get; set; }

        /// <summary>转换是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>转换是否已触发（用于一次性转换）。</summary>
        [JsonIgnore]
        public bool HasFired { get; set; }

        /// <summary>转换条件节点图（评估是否可以执行转换）。</summary>
        public NodeGraph? ConditionGraph { get; set; }

        /// <summary>转换触发时执行的动作节点图。</summary>
        public NodeGraph? ActionGraph { get; set; }

        /// <summary>转换注释。</summary>
        public string? Comment { get; set; }

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 评估转换条件。
        /// </summary>
        /// <returns>条件是否满足。</returns>
        public bool EvaluateCondition(Dictionary<Guid, object?> context)
        {
            if (ConditionGraph == null) return true;
            if (!IsEnabled) return false;
            if (IsOneShot && HasFired) return false;

            try
            {
                ConditionGraph.ExecuteGraph(context);

                // 检查条件图的输出值
                // 简化处理：如果条件图有输出节点 "Result"，使用其值
                var resultNode = ConditionGraph.Nodes.FirstOrDefault(n => n.NodeType == "Branch" || n.OutputPins.Any(p => p.Name == "Result"));
                return resultNode != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 执行转换动作。
        /// </summary>
        public void ExecuteAction(Dictionary<Guid, object?> context)
        {
            if (ActionGraph != null)
            {
                ActionGraph.ExecuteGraph(context);
            }

            if (IsOneShot)
            {
                HasFired = true;
            }
        }

        /// <summary>
        /// 创建一个新的转换。
        /// </summary>
        public static TransitionGraphNode Create(Guid sourceState, Guid sourcePin, Guid targetState, string name = "Transition")
        {
            return new TransitionGraphNode
            {
                SourceStateId = sourceState,
                SourcePinId = sourcePin,
                TargetStateId = targetState,
                TransitionName = name
            };
        }

        /// <summary>
        /// 序列化为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化。
        /// </summary>
        public static TransitionGraphNode? FromJson(string json)
        {
            return JsonSerializer.Deserialize<TransitionGraphNode>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  StateMachineGraph - 状态机图
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机图：可视化管理状态机的状态节点和转换关系。
    /// </summary>
    [Serializable]
    public class StateMachineGraph
    {
        /// <summary>状态机图唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>状态机图名称。</summary>
        public string Name { get; set; } = "NewStateMachine";

        /// <summary>状态机图显示标题。</summary>
        public string DisplayName { get; set; } = "New State Machine";

        /// <summary>状态机图描述。</summary>
        public string? Description { get; set; }

        /// <summary>状态节点列表。</summary>
        public List<StateGraphNode> States { get; set; } = new List<StateGraphNode>();

        /// <summary>转换节点列表。</summary>
        public List<TransitionGraphNode> Transitions { get; set; } = new List<TransitionGraphNode>();

        /// <summary>初始状态节点 ID。</summary>
        public Guid InitialStateId { get; set; }

        /// <summary>任意状态节点 ID（可选）。</summary>
        public Guid? AnyStateId { get; set; }

        /// <summary>图的自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>图版本号。</summary>
        public int Version { get; set; }

        // ───── 状态操作 ─────

        /// <summary>添加状态节点。</summary>
        public StateGraphNode AddState(StateGraphNode state)
        {
            States.Add(state);
            if (state.IsInitialState && InitialStateId == Guid.Empty)
            {
                InitialStateId = state.Id;
            }
            return state;
        }

        /// <summary>移除状态节点及其所有转换。</summary>
        public bool RemoveState(Guid stateId)
        {
            // 移除与该状态相关的所有转换
            Transitions.RemoveAll(t => t.SourceStateId == stateId || t.TargetStateId == stateId);

            // 移除状态
            var state = States.FirstOrDefault(s => s.Id == stateId);
            if (state == null) return false;

            States.Remove(state);

            // 如果移除的是初始状态，需要重新指定
            if (InitialStateId == stateId)
            {
                InitialStateId = States.FirstOrDefault(s => s.IsInitialState)?.Id ?? Guid.Empty;
            }

            return true;
        }

        /// <summary>通过 ID 获取状态节点。</summary>
        public StateGraphNode? GetState(Guid stateId)
        {
            return States.FirstOrDefault(s => s.Id == stateId);
        }

        /// <summary>通过名称获取状态节点。</summary>
        public StateGraphNode? GetStateByName(string name)
        {
            return States.FirstOrDefault(s => s.StateName == name);
        }

        /// <summary>设置初始状态。</summary>
        public void SetInitialState(Guid stateId)
        {
            foreach (var state in States)
            {
                state.IsInitialState = (state.Id == stateId);
            }
            InitialStateId = stateId;
        }

        // ───── 转换操作 ─────

        /// <summary>创建转换。</summary>
        public TransitionGraphNode? AddTransition(Guid sourceState, Guid sourcePin, Guid targetState, string name = "Transition")
        {
            var srcState = GetState(sourceState);
            var tgtState = GetState(targetState);
            if (srcState == null || tgtState == null) return null;

            var transition = TransitionGraphNode.Create(sourceState, sourcePin, targetState, name);
            transition.Priority = Transitions.Count;
            Transitions.Add(transition);

            // 更新源引脚的连接列表
            var pin = srcState.GetTransitionPin(sourcePin);
            pin?.TransitionIds.Add(transition.Id);

            return transition;
        }

        /// <summary>移除转换。</summary>
        public bool RemoveTransition(Guid transitionId)
        {
            var transition = Transitions.FirstOrDefault(t => t.Id == transitionId);
            if (transition == null) return false;

            // 清理源引脚的连接
            var srcState = GetState(transition.SourceStateId);
            var pin = srcState?.GetTransitionPin(transition.SourcePinId);
            pin?.TransitionIds.Remove(transitionId);

            Transitions.Remove(transition);
            return true;
        }

        /// <summary>通过 ID 获取转换。</summary>
        public TransitionGraphNode? GetTransition(Guid transitionId)
        {
            return Transitions.FirstOrDefault(t => t.Id == transitionId);
        }

        /// <summary>获取从指定状态出发的所有转换。</summary>
        public List<TransitionGraphNode> GetTransitionsFromState(Guid stateId)
        {
            return Transitions.Where(t => t.SourceStateId == stateId).OrderBy(t => t.Priority).ToList();
        }

        /// <summary>获取到达指定状态的所有转换。</summary>
        public List<TransitionGraphNode> GetTransitionsToState(Guid stateId)
        {
            return Transitions.Where(t => t.TargetStateId == stateId).ToList();
        }

        // ───── 验证 ─────

        /// <summary>
        /// 验证状态机图完整性。
        /// </summary>
        public List<GraphValidationMessage> Validate()
        {
            var messages = new List<GraphValidationMessage>();

            // 1. 检查初始状态
            if (InitialStateId == Guid.Empty)
            {
                messages.Add(new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Error,
                    Message = "状态机没有设置初始状态",
                    Suggestion = "请选择一个状态节点作为初始状态"
                });
            }
            else if (States.FirstOrDefault(s => s.Id == InitialStateId) == null)
            {
                messages.Add(new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Error,
                    Message = "初始状态 ID 引用了不存在的状态节点",
                    Suggestion = "请重新设置初始状态"
                });
            }

            // 2. 检查无效转换引用
            foreach (var trans in Transitions)
            {
                if (States.FirstOrDefault(s => s.Id == trans.SourceStateId) == null)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        Message = $"转换 '{trans.TransitionName}' 的源状态不存在",
                        Suggestion = "请删除此无效转换"
                    });
                }
                if (States.FirstOrDefault(s => s.Id == trans.TargetStateId) == null)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        Message = $"转换 '{trans.TransitionName}' 的目标状态不存在",
                        Suggestion = "请删除此无效转换"
                    });
                }
            }

            // 3. 检查孤立状态（无转换连接）
            foreach (var state in States)
            {
                var hasOutgoing = Transitions.Any(t => t.SourceStateId == state.Id);
                var hasIncoming = Transitions.Any(t => t.TargetStateId == state.Id);

                if (!hasOutgoing && !hasIncoming && !state.IsInitialState)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Warning,
                        Message = $"状态 '{state.StateName}' 没有任何转换连接，是孤立状态",
                        Suggestion = "如果该状态不再需要，可以安全删除"
                    });
                }
            }

            // 4. 检查名称唯一性
            var stateNames = States.GroupBy(s => s.StateName).Where(g => g.Count() > 1);
            foreach (var group in stateNames)
            {
                messages.Add(new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Warning,
                    Message = $"存在重复的状态名: '{group.Key}'",
                    Suggestion = "请重命名其中一个状态以避免冲突"
                });
            }

            return messages;
        }

        // ───── 序列化 ─────

        /// <summary>序列化为 JSON。</summary>
        public string SerializeToJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Serialize(this, options);
        }

        /// <summary>从 JSON 反序列化。</summary>
        public static StateMachineGraph? DeserializeFromJson(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Deserialize<StateMachineGraph>(json, options);
        }

        /// <summary>保存到文件。</summary>
        public void SaveToFile(string filePath)
        {
            System.IO.File.WriteAllText(filePath, SerializeToJson());
        }

        /// <summary>从文件加载。</summary>
        public static StateMachineGraph? LoadFromFile(string filePath)
        {
            return DeserializeFromJson(System.IO.File.ReadAllText(filePath));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  StateMachineInstance - 运行时状态机实例
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机运行时实例：将状态机图实例化为可执行的状态机。
    /// </summary>
    public class StateMachineInstance
    {
        /// <summary>关联的状态机图。</summary>
        public StateMachineGraph Graph { get; }

        /// <summary>当前状态 ID。</summary>
        public Guid CurrentStateId { get; private set; }

        /// <summary>上一帧的状态 ID。</summary>
        public Guid PreviousStateId { get; private set; }

        /// <summary>状态机是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>总运行时间。</summary>
        public float TotalTime { get; private set; }

        /// <summary>当前状态持续时间。</summary>
        public float CurrentStateTime { get; private set; }

        /// <summary>转换历史记录。</summary>
        public List<StateTransitionRecord> TransitionHistory { get; } = new List<StateTransitionRecord>();

        /// <summary>每帧执行上下文。</summary>
        public Dictionary<Guid, object?> Context { get; } = new Dictionary<Guid, object?>();

        /// <summary>状态变化事件。</summary>
        public event Action<Guid, Guid>? OnStateChanged;

        /// <summary>转换触发事件。</summary>
        public event Action<StateTransitionRecord>? OnTransition;

        /// <summary>
        /// 创建状态机实例。
        /// </summary>
        public StateMachineInstance(StateMachineGraph graph)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            CurrentStateId = Guid.Empty;
            PreviousStateId = Guid.Empty;
        }

        /// <summary>
        /// 启动状态机（进入初始状态）。
        /// </summary>
        public void Start()
        {
            if (Graph.InitialStateId == Guid.Empty)
            {
                throw new InvalidOperationException("状态机没有设置初始状态");
            }

            IsRunning = true;
            TotalTime = 0;
            CurrentStateTime = 0;
            TransitionHistory.Clear();

            EnterState(Graph.InitialStateId);
        }

        /// <summary>
        /// 停止状态机。
        /// </summary>
        public void Stop()
        {
            if (CurrentStateId != Guid.Empty)
            {
                ExitState(CurrentStateId);
            }
            IsRunning = false;
        }

        /// <summary>
        /// 每帧更新状态机。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间。</param>
        public void Update(float deltaTime)
        {
            if (!IsRunning) return;

            TotalTime += deltaTime;
            CurrentStateTime += deltaTime;

            // 更新当前状态
            UpdateState(CurrentStateId, deltaTime);

            // 评估转换条件
            EvaluateTransitions();
        }

        /// <summary>
        /// 手动触发转换到指定状态。
        /// </summary>
        public bool ForceTransition(Guid targetStateId)
        {
            if (!IsRunning) return false;
            var target = Graph.GetState(targetStateId);
            if (target == null) return false;

            var record = new StateTransitionRecord
            {
                TransitionName = "Forced",
                SourceStateId = CurrentStateId,
                TargetStateId = targetStateId,
                Timestamp = TotalTime
            };

            PerformTransition(record);
            return true;
        }

        /// <summary>
        /// 获取当前状态节点。
        /// </summary>
        public StateGraphNode? GetCurrentState()
        {
            return Graph.GetState(CurrentStateId);
        }

        /// <summary>
        /// 获取当前状态名称。
        /// </summary>
        public string GetCurrentStateName()
        {
            return GetCurrentState()?.StateName ?? "None";
        }

        // ── 内部方法 ──

        private void EnterState(Guid stateId)
        {
            var state = Graph.GetState(stateId);
            if (state == null) return;

            state.OnEnterGraph?.ExecuteGraph(Context);
            CurrentStateTime = 0;
        }

        private void UpdateState(Guid stateId, float deltaTime)
        {
            var state = Graph.GetState(stateId);
            if (state == null) return;

            state.OnUpdateGraph?.ExecuteGraph(Context);
        }

        private void ExitState(Guid stateId)
        {
            var state = Graph.GetState(stateId);
            if (state == null) return;

            state.OnExitGraph?.ExecuteGraph(Context);
        }

        private void EvaluateTransitions()
        {
            // 从当前状态和任意状态获取所有转换
            var transitions = Graph.GetTransitionsFromState(CurrentStateId).ToList();

            if (Graph.AnyStateId.HasValue)
            {
                transitions.AddRange(Graph.GetTransitionsFromState(Graph.AnyStateId.Value));
            }

            // 按优先级排序
            transitions = transitions.OrderBy(t => t.Priority).ToList();

            foreach (var transition in transitions)
            {
                if (transition.EvaluateCondition(Context))
                {
                    var record = new StateTransitionRecord
                    {
                        TransitionName = transition.TransitionName,
                        TransitionId = transition.Id,
                        SourceStateId = CurrentStateId,
                        TargetStateId = transition.TargetStateId,
                        Priority = transition.Priority,
                        Timestamp = TotalTime
                    };

                    PerformTransition(record);
                    transition.ExecuteAction(Context);
                    break; // 每帧最多执行一次转换
                }
            }
        }

        private void PerformTransition(StateTransitionRecord record)
        {
            PreviousStateId = CurrentStateId;
            var oldStateId = CurrentStateId;

            // 退出旧状态
            ExitState(oldStateId);

            // 进入新状态
            CurrentStateId = record.TargetStateId;
            EnterState(CurrentStateId);

            // 记录历史
            TransitionHistory.Add(record);

            // 触发事件
            OnTransition?.Invoke(record);
            OnStateChanged?.Invoke(oldStateId, CurrentStateId);
        }
    }

    /// <summary>
    /// 状态转换记录：记录一次状态转换的详细信息。
    /// </summary>
    [Serializable]
    public class StateTransitionRecord
    {
        /// <summary>转换名称。</summary>
        public string TransitionName { get; set; } = string.Empty;

        /// <summary>转换 ID。</summary>
        public Guid TransitionId { get; set; }

        /// <summary>源状态 ID。</summary>
        public Guid SourceStateId { get; set; }

        /// <summary>目标状态 ID。</summary>
        public Guid TargetStateId { get; set; }

        /// <summary>转换优先级。</summary>
        public int Priority { get; set; }

        /// <summary>转换时间戳。</summary>
        public float Timestamp { get; set; }

        /// <summary>转换时当前状态的持续时间。</summary>
        public float StateDuration { get; set; }

        /// <summary>附加数据。</summary>
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();

        public override string ToString() =>
            $"[{Timestamp:F2}s] {TransitionName}: {SourceStateId:N4} -> {TargetStateId:N4} (Priority: {Priority})";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  StateMachineDebugger - 状态机调试器
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机调试器：提供运行时状态机调试功能。
    /// </summary>
    public class StateMachineDebugger
    {
        /// <summary>关联的状态机实例。</summary>
        public StateMachineInstance Instance { get; }

        /// <summary>是否启用调试。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>调试日志列表。</summary>
        public List<string> DebugLog { get; } = new List<string>();

        /// <summary>最大日志条数。</summary>
        public int MaxLogEntries { get; set; } = 1000;

        /// <summary>需要高亮的状态节点 ID 集合。</summary>
        public HashSet<Guid> HighlightedStates { get; } = new HashSet<Guid>();

        /// <summary>需要高亮的转换 ID 集合。</summary>
        public HashSet<Guid> HighlightedTransitions { get; } = new HashSet<Guid>();

        /// <summary>最后评估的转换条件值。</summary>
        public Dictionary<Guid, bool> LastConditionValues { get; } = new Dictionary<Guid, bool>();

        /// <summary>当前显示的转换历史索引（-1 = 不显示任何历史）。</summary>
        public int HistoryViewIndex { get; set; } = -1;

        /// <summary>状态持续时间记录。</summary>
        public Dictionary<Guid, float> StateDurations { get; } = new Dictionary<Guid, float>();

        /// <summary>是否启用断点（在转换前暂停）。</summary>
        public bool BreakOnTransition { get; set; }

        /// <summary>是否启用状态时间追踪。</summary>
        public bool TrackStateDurations { get; set; } = true;

        /// <summary>
        /// 创建调试器。
        /// </summary>
        public StateMachineDebugger(StateMachineInstance instance)
        {
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
            Instance.OnTransition += OnTransitionFired;
            Instance.OnStateChanged += OnStateChanged;
        }

        /// <summary>
        /// 获取当前状态信息。
        /// </summary>
        public string GetCurrentStateInfo()
        {
            var state = Instance.GetCurrentState();
            if (state == null) return "No active state";

            return $"State: {state.StateName} | Time: {Instance.CurrentStateTime:F2}s | Total: {Instance.TotalTime:F2}s";
        }

        /// <summary>
        /// 获取转换历史（格式化文本）。
        /// </summary>
        public string GetTransitionHistoryText(int maxEntries = 50)
        {
            var entries = Instance.TransitionHistory
                .Skip(Math.Max(0, Instance.TransitionHistory.Count - maxEntries))
                .ToList();

            if (entries.Count == 0) return "No transitions recorded.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== State Machine Transition History ===");
            foreach (var record in entries)
            {
                var srcState = Instance.Graph.GetState(record.SourceStateId);
                var tgtState = Instance.Graph.GetState(record.TargetStateId);
                var srcName = srcState?.StateName ?? "Unknown";
                var tgtName = tgtState?.StateName ?? "Unknown";

                sb.AppendLine($"[{record.Timestamp:F3}s] {record.TransitionName}: {srcName} -> {tgtName} (Priority: {record.Priority})");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 获取所有状态的总持续时间统计。
        /// </summary>
        public string GetStateDurationStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== State Duration Statistics ===");

            foreach (var state in Instance.Graph.States)
            {
                var totalDur = Instance.TransitionHistory
                    .Where(r => r.SourceStateId == state.Id)
                    .Sum(r => r.StateDuration);
                sb.AppendLine($"{state.StateName}: {totalDur:F2}s total");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 获取调试面板数据。
        /// </summary>
        public Dictionary<string, object> GetDebugPanelData()
        {
            var data = new Dictionary<string, object>
            {
                ["CurrentState"] = Instance.GetCurrentStateName(),
                ["CurrentStateId"] = Instance.CurrentStateId,
                ["IsRunning"] = Instance.IsRunning,
                ["TotalTime"] = Instance.TotalTime,
                ["CurrentStateTime"] = Instance.CurrentStateTime,
                ["TransitionCount"] = Instance.TransitionHistory.Count,
                ["HistoryViewIndex"] = HistoryViewIndex
            };

            if (HistoryViewIndex >= 0 && HistoryViewIndex < Instance.TransitionHistory.Count)
            {
                var record = Instance.TransitionHistory[HistoryViewIndex];
                data["ViewedTransition"] = record.ToString();
            }

            return data;
        }

        private void OnTransitionFired(StateTransitionRecord record)
        {
            if (!IsEnabled) return;

            var srcState = Instance.Graph.GetState(record.SourceStateId);
            var tgtState = Instance.Graph.GetState(record.TargetStateId);

            var log = $"[TRANSITION] {record.TransitionName}: {srcState?.StateName ?? "?"} -> {tgtState?.StateName ?? "?"} @ {record.Timestamp:F3}s";
            DebugLog.Add(log);

            HighlightedTransitions.Add(record.TransitionId);
            HighlightedStates.Add(record.TargetStateId);

            // 记录状态持续时间
            if (TrackStateDurations)
            {
                record.StateDuration = Instance.CurrentStateTime;
                if (!StateDurations.ContainsKey(record.SourceStateId))
                    StateDurations[record.SourceStateId] = 0;
                StateDurations[record.SourceStateId] += record.StateDuration;
            }

            // 限制日志大小
            while (DebugLog.Count > MaxLogEntries)
            {
                DebugLog.RemoveAt(0);
            }
        }

        private void OnStateChanged(Guid oldStateId, Guid newStateId)
        {
            if (!IsEnabled) return;

            var oldState = Instance.Graph.GetState(oldStateId);
            var newState = Instance.Graph.GetState(newStateId);

            DebugLog.Add($"[STATE] Changed: {oldState?.StateName ?? "?"} -> {newState?.StateName ?? "?"}");

            while (DebugLog.Count > MaxLogEntries)
            {
                DebugLog.RemoveAt(0);
            }
        }

        /// <summary>
        /// 清除调试日志和高亮。
        /// </summary>
        public void ClearLog()
        {
            DebugLog.Clear();
            HighlightedStates.Clear();
            HighlightedTransitions.Clear();
            LastConditionValues.Clear();
        }
    }
}
