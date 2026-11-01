// ============================================================================
//  YamaEngine.AI.GOAPPlanner.cs
//  yama引擎 -- GOAP 规划器 (Goal-Oriented Action Planning)
//  对齐 F.E.A.R. GOAP / Orkin GDC 2005 / UE5 Gameplay Ability System
// ----------------------------------------------------------------------------
//  GOAP 用 A* 在状态空间搜索：从当前世界状态出发，每次尝试一个 action，
//  直到达到 goal 指定的目标状态。返回 action 序列。
//
//  本文件实现：
//    1. WorldState            - 位掩码式世界状态(支持 bool/int/float)
//    2. GOAPActionV2          - 动作(前置/效果/代价/上下文验证)
//    3. GOAPGoalV2            - 目标(期望状态/优先级/动态)
//    4. GOAPNode              - A* 搜索节点
//    5. GOAPPlannerV2         - 规划器(A* 状态空间 + 启发函数)
//    6. GOAPExecutor          - 执行器(执行 plan + 中断 + 重规划)
//    7. GOAPMemory            - 短期记忆(避免重复规划)
//    8. GOAPDomainBuilder     - 流式 API 构建 domain
// ----------------------------------------------------------------------------
//  注意：状态空间搜索爆炸性很强，得限制搜索深度和节点数。
// ============================================================================
using System;
using System.Collections.Generic;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、世界状态
    // ========================================================================

    /// <summary>
    /// 世界状态。用 Dictionary 存键值对，简化版。
    /// 完整实现会用位掩码压缩，这里用对象换可读性。
    /// </summary>
    public sealed class WorldState
    {
        private readonly Dictionary<string, bool> _bools = new();
        private readonly Dictionary<string, int> _ints = new();
        private readonly Dictionary<string, float> _floats = new();
        private readonly Dictionary<string, string> _strings = new();

        public int Count => _bools.Count + _ints.Count + _floats.Count + _strings.Count;

        // Bool
        public bool GetBool(string key, bool def = false) => _bools.TryGetValue(key, out var v) ? v : def;
        public void SetBool(string key, bool v) => _bools[key] = v;
        public bool HasBool(string key) => _bools.ContainsKey(key);

        // Int
        public int GetInt(string key, int def = 0) => _ints.TryGetValue(key, out var v) ? v : def;
        public void SetInt(string key, int v) => _ints[key] = v;
        public bool HasInt(string key) => _ints.ContainsKey(key);

        // Float
        public float GetFloat(string key, float def = 0f) => _floats.TryGetValue(key, out var v) ? v : def;
        public void SetFloat(string key, float v) => _floats[key] = v;
        public bool HasFloat(string key) => _floats.ContainsKey(key);

        // String
        public string GetString(string key, string def = "") => _strings.TryGetValue(key, out var v) ? v : def;
        public void SetString(string key, string v) => _strings[key] = v;

        /// <summary>克隆。</summary>
        public WorldState Clone()
        {
            var c = new WorldState();
            foreach (var (k, v) in _bools) c._bools[k] = v;
            foreach (var (k, v) in _ints) c._ints[k] = v;
            foreach (var (k, v) in _floats) c._floats[k] = v;
            foreach (var (k, v) in _strings) c._strings[k] = v;
            return c;
        }

        /// <summary>计算两个状态的差异(用于启发函数)。差异越大距离越远。</summary>
        public int DistanceTo(WorldState other)
        {
            int diff = 0;
            foreach (var (k, v) in _bools)
                if (other.GetBool(k) != v) diff++;
            foreach (var (k, v) in _ints)
                if (other.GetInt(k) != v) diff++;
            foreach (var (k, v) in _floats)
                if (MathF.Abs(other.GetFloat(k) - v) > 0.01f) diff++;
            return diff;
        }

        /// <summary>判断是否满足另一个状态的所有条件(只检查 other 里有的 key)。</summary>
        public bool Satisfies(WorldState other)
        {
            foreach (var (k, v) in other._bools)
                if (GetBool(k) != v) return false;
            foreach (var (k, v) in other._ints)
                if (GetInt(k) != v) return false;
            foreach (var (k, v) in other._floats)
                if (MathF.Abs(GetFloat(k) - v) > 0.01f) return false;
            return true;
        }

        /// <summary>应用 effects 到当前状态(返回新状态)。</summary>
        public WorldState ApplyEffects(WorldState effects)
        {
            var result = Clone();
            foreach (var (k, v) in effects._bools) result._bools[k] = v;
            foreach (var (k, v) in effects._ints) result._ints[k] = v;
            foreach (var (k, v) in effects._floats) result._floats[k] = v;
            foreach (var (k, v) in effects._strings) result._strings[k] = v;
            return result;
        }

        /// <summary>状态指纹(用于去重/hash)。</summary>
        public string Fingerprint()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (k, v) in _bools) sb.Append(k).Append('=').Append(v).Append(';');
            foreach (var (k, v) in _ints) sb.Append(k).Append('=').Append(v).Append(';');
            foreach (var (k, v) in _floats) sb.Append(k).Append('=').Append(v.ToString("F2")).Append(';');
            return sb.ToString();
        }
    }

    // ========================================================================
    //  二、Action 和 Goal
    // ========================================================================

    /// <summary>
    /// GOAP 动作。包含前置条件、效果、代价、上下文验证器。
    /// </summary>
    public sealed class GOAPActionV2
    {
        public string Name;
        /// <summary>基础代价。最终代价 = BaseCost * CostMultiplier(context)。</summary>
        public float BaseCost = 1f;
        /// <summary>前置条件(必须全部满足才能执行)。</summary>
        public WorldState Preconditions = new();
        /// <summary>执行后世界状态的变化。</summary>
        public WorldState Effects = new();
        /// <summary>上下文验证器(检查 agent 当前能否执行，比如距离够不够)。</summary>
        public Func<GOAPContext, bool>? Validator;
        /// <summary>动态代价(根据上下文调整代价)。</summary>
        public Func<GOAPContext, float>? CostMultiplier;
        /// <summary>执行器(实际执行动作)。</summary>
        public Func<GOAPContext, bool>? Executor;
        /// <summary>是否可中断。</summary>
        public bool Interruptible = true;
        /// <summary>预计执行时长(秒)。</summary>
        public float EstimatedDuration = 1f;
        /// <summary>动作所需标签(用于智能对象匹配)。</summary>
        public string[] RequiredTags = Array.Empty<string>();

        public GOAPActionV2(string name) { Name = name; }

        /// <summary>检查前置条件是否满足。</summary>
        public bool PreconditionsMet(WorldState state) => state.Satisfies(Preconditions);

        /// <summary>检查能否执行(包括上下文验证)。</summary>
        public bool CanExecute(GOAPContext context)
        {
            if (!PreconditionsMet(context.WorldState)) return false;
            if (Validator != null && !Validator(context)) return false;
            return true;
        }

        /// <summary>获取实际代价。</summary>
        public float GetCost(GOAPContext context)
        {
            float mult = CostMultiplier?.Invoke(context) ?? 1f;
            return BaseCost * mult;
        }

        /// <summary>执行动作。</summary>
        public bool Execute(GOAPContext context)
        {
            if (Executor == null) return true;
            return Executor(context);
        }

        public override string ToString() => $"GOAPAction('{Name}', cost={BaseCost})";
    }

    /// <summary>
    /// GOAP 目标。描述"想要达到的世界状态"。
    /// </summary>
    public sealed class GOAPGoalV2
    {
        public string Name;
        /// <summary>期望达到的世界状态。</summary>
        public WorldState DesiredState = new();
        /// <summary>静态优先级。</summary>
        public float Priority = 1f;
        /// <summary>动态优先级(每帧重算)。</summary>
        public Func<GOAPContext, float>? DynamicPriority;
        /// <summary>目标是否当前有效(冷却/条件检查)。</summary>
        public Func<GOAPContext, bool>? IsValidFunc;
        /// <summary>冷却时间(秒)。被放弃后多久能再选。</summary>
        public float CoolDown = 0f;
        private float _lastSelectedTime = -1e9f;

        public GOAPGoalV2(string name) { Name = name; }

        /// <summary>获取当前优先级。</summary>
        public float GetPriority(GOAPContext ctx)
        {
            if (DynamicPriority != null) return DynamicPriority(ctx);
            return Priority;
        }

        /// <summary>是否有效。</summary>
        public bool IsValid(GOAPContext ctx, float currentTime)
        {
            if (currentTime - _lastSelectedTime < CoolDown) return false;
            if (IsValidFunc != null && !IsValidFunc(ctx)) return false;
            // 目标已经达成就不需要再规划
            if (ctx.WorldState.Satisfies(DesiredState)) return false;
            return true;
        }

        internal void MarkSelected(float time) => _lastSelectedTime = time;

        public override string ToString() => $"GOAPGoal('{Name}', pri={Priority})";
    }

    // ========================================================================
    //  三、执行上下文
    // ========================================================================

    /// <summary>
    /// GOAP 执行上下文。规划/执行时传给 action。
    /// </summary>
    public sealed class GOAPContext
    {
        public int AgentId;
        public WorldState WorldState;
        public object? AgentObject;
        public float CurrentTime;
        /// <summary>感知/记忆系统提供的数据(可见目标/最近声音等)。</summary>
        public Dictionary<string, object> Memory = new();
        /// <summary>智能对象系统提供的可用交互点。</summary>
        public List<SmartObjectRef> AvailableSmartObjects = new();

        public GOAPContext(int agentId, WorldState worldState)
        {
            AgentId = agentId;
            WorldState = worldState;
        }

        public T? GetMemory<T>(string key) where T : class
            => Memory.TryGetValue(key, out var v) && v is T t ? t : null;

        public void SetMemory(string key, object value) => Memory[key] = value;
    }

    /// <summary>智能对象引用(用于 action 匹配)。</summary>
    public struct SmartObjectRef
    {
        public int Id;
        public string[] Tags;
        public object Instance;
    }

    // ========================================================================
    //  四、A* 搜索节点
    // ========================================================================

    /// <summary>状态空间搜索节点。</summary>
    internal sealed class GOAPNode
    {
        public WorldState State;
        public GOAPActionV2? Action;
        public GOAPNode? Parent;
        public float G; // 已花费代价
        public float H; // 启发值
        public float F => G + H;
        public int Depth;

        public GOAPNode(WorldState state, GOAPNode? parent, GOAPActionV2? action, float g, float h, int depth)
        {
            State = state; Parent = parent; Action = action;
            G = g; H = h; Depth = depth;
        }
    }

    // ========================================================================
    //  五、GOAP 规划器
    // ========================================================================

    /// <summary>
    /// GOAP 规划器。用 A* 在状态空间搜索。
    /// 每个节点是世界状态，每条边是一个 action。
    /// </summary>
    public sealed class GOAPPlannerV2
    {
        private readonly List<GOAPActionV2> _actions = new();
        /// <summary>搜索深度上限(防止状态爆炸)。</summary>
        public int MaxDepth = 12;
        /// <summary>最大扩展节点数。</summary>
        public int MaxNodes = 2000;
        /// <summary>启发函数权重。</summary>
        public float HeuristicWeight = 1f;
        /// <summary>规划统计。</summary>
        public int LastPlanNodesExpanded;
        public int LastPlanNodesGenerated;
        public float LastPlanTimeMs;

        public GOAPPlannerV2() { }

        public void RegisterAction(GOAPActionV2 action) => _actions.Add(action);
        public void RemoveAction(string name) => _actions.RemoveAll(a => a.Name == name);
        public void ClearActions() => _actions.Clear();

        /// <summary>
        /// 规划从当前状态到 goal 的 action 序列。
        /// </summary>
        public List<GOAPActionV2>? Plan(GOAPGoalV2 goal, GOAPContext context)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LastPlanNodesExpanded = 0;
            LastPlanNodesGenerated = 0;

            var start = new GOAPNode(context.WorldState.Clone(), null, null, 0, CalcHeuristic(context.WorldState, goal.DesiredState), 0);

            // 优先队列(按 F 排序)
            var open = new List<GOAPNode> { start };
            var openSet = new Dictionary<string, GOAPNode> { [start.State.Fingerprint()] = start };
            var closed = new HashSet<string>();

            GOAPNode? bestNode = null;
            float bestH = start.H;

            while (open.Count > 0)
            {
                // 找 F 最小的(简化：线性扫描，节点数不大时够用)
                int minIdx = 0;
                for (int i = 1; i < open.Count; i++)
                    if (open[i].F < open[minIdx].F) minIdx = i;
                var cur = open[minIdx];
                open.RemoveAt(minIdx);
                openSet.Remove(cur.State.Fingerprint());
                LastPlanNodesExpanded++;

                // 检查是否达成目标
                if (cur.State.Satisfies(goal.DesiredState))
                {
                    bestNode = cur;
                    break;
                }

                if (cur.Depth >= MaxDepth || LastPlanNodesExpanded >= MaxNodes)
                {
                    // 记录最接近的节点(给部分 plan)
                    if (cur.H < bestH) { bestH = cur.H; bestNode = cur; }
                    continue;
                }

                closed.Add(cur.State.Fingerprint());

                // 扩展所有可执行的 action
                foreach (var action in _actions)
                {
                    if (!action.PreconditionsMet(cur.State)) continue;
                    var newState = cur.State.ApplyEffects(action.Effects);
                    var finger = newState.Fingerprint();
                    if (closed.Contains(finger)) continue;

                    float cost = action.GetCost(context);
                    float g = cur.G + cost;
                    float h = CalcHeuristic(newState, goal.DesiredState) * HeuristicWeight;

                    if (openSet.TryGetValue(finger, out var existing))
                    {
                        if (g < existing.G)
                        {
                            existing.G = g;
                            existing.Parent = cur;
                            existing.Action = action;
                            existing.Depth = cur.Depth + 1;
                        }
                    }
                    else
                    {
                        var node = new GOAPNode(newState, cur, action, g, h, cur.Depth + 1);
                        open.Add(node);
                        openSet[finger] = node;
                        LastPlanNodesGenerated++;
                    }
                }
            }

            sw.Stop();
            LastPlanTimeMs = sw.ElapsedMilliseconds;

            if (bestNode == null) return null;

            // 回溯
            var plan = new List<GOAPActionV2>();
            var n = bestNode;
            while (n?.Action != null)
            {
                plan.Add(n.Action);
                n = n.Parent;
            }
            plan.Reverse();
            return plan;
        }

        /// <summary>启发函数：当前状态到目标的"差异"数。</summary>
        private float CalcHeuristic(WorldState state, WorldState goal)
        {
            return state.DistanceTo(goal);
        }

        /// <summary>挑选优先级最高的有效目标。</summary>
        public GOAPGoalV2? PickGoal(List<GOAPGoalV2> goals, GOAPContext context)
        {
            GOAPGoalV2? best = null;
            float bestP = float.MinValue;
            foreach (var g in goals)
            {
                if (!g.IsValid(context, context.CurrentTime)) continue;
                float p = g.GetPriority(context);
                if (p > bestP) { bestP = p; best = g; }
            }
            best?.MarkSelected(context.CurrentTime);
            return best;
        }
    }

    // ========================================================================
    //  六、GOAP 执行器
    // ========================================================================

    /// <summary>GOAP 执行状态。</summary>
    public enum GOAPExecState
    {
        Idle,
        Planning,
        MovingTo,
        Executing,
        Waiting,
        Completed,
        Failed,
        Aborted
    }

    /// <summary>
    /// GOAP 执行器。管理 plan 的执行、action 的执行进度、中断和重规划。
    /// </summary>
    public sealed class GOAPExecutor
    {
        private readonly GOAPPlannerV2 _planner;
        private readonly List<GOAPGoalV2> _goals;
        private GOAPContext? _context;

        private Queue<GOAPActionV2>? _plan;
        private GOAPActionV2? _currentAction;
        private GOAPGoalV2? _currentGoal;
        private GOAPExecState _state = GOAPExecState.Idle;
        private float _actionTimer;
        private float _replanTimer;
        private int _replanAttempts;

        public GOAPExecState State => _state;
        public GOAPGoalV2? CurrentGoal => _currentGoal;
        public GOAPActionV2? CurrentAction => _currentAction;
        public Queue<GOAPActionV2>? CurrentPlan => _plan;
        public bool HasPlan => _plan != null && _plan.Count > 0;
        public float PlanProgress
        {
            get
            {
                if (_plan == null) return 0;
                int total = _plan.Count + (_currentAction != null ? 1 : 0);
                if (total == 0) return 1;
                return 1f - (float)_plan.Count / total;
            }
        }

        /// <summary>重规划冷却(秒)。失败后等多久再试。</summary>
        public float ReplanCooldown = 0.5f;
        /// <summary>最大重规划次数。</summary>
        public int MaxReplanAttempts = 3;
        /// <summary>是否在 action 失败时自动重规划。</summary>
        public bool AutoReplan = true;
        /// <summary>每帧检查 plan 是否还有效。</summary>
        public bool ValidatePlanEachTick = true;

        public event Action<GOAPGoalV2?>? OnGoalChanged;
        public event Action<List<GOAPActionV2>?>? OnPlanChanged;
        public event Action<GOAPActionV2>? OnActionStarted;
        public event Action<GOAPActionV2, bool>? OnActionFinished;
        public event Action<GOAPExecState>? OnStateChanged;

        public GOAPExecutor(GOAPPlannerV2 planner, List<GOAPGoalV2> goals)
        {
            _planner = planner;
            _goals = goals;
        }

        public void SetContext(GOAPContext context) => _context = context;

        /// <summary>每帧更新。</summary>
        public void Update(float dt)
        {
            if (_context == null) return;
            _context.CurrentTime += dt;

            switch (_state)
            {
                case GOAPExecState.Idle:
                    TryStartNewPlan();
                    break;
                case GOAPExecState.Planning:
                    // 同步规划已经在 TryStartNewPlan 完成
                    break;
                case GOAPExecState.Executing:
                    UpdateExecuting(dt);
                    break;
                case GOAPExecState.Waiting:
                    _actionTimer -= dt;
                    if (_actionTimer <= 0) SetState(GOAPExecState.Idle);
                    break;
                case GOAPExecState.Failed:
                    _replanTimer -= dt;
                    if (_replanTimer <= 0 && AutoReplan)
                    {
                        _replanAttempts++;
                        if (_replanAttempts <= MaxReplanAttempts)
                            TryStartNewPlan();
                        else
                            SetState(GOAPExecState.Idle);
                    }
                    break;
            }

            // 定期验证 plan
            if (ValidatePlanEachTick && _state == GOAPExecState.Executing && _currentAction != null)
            {
                if (!_currentAction.CanExecute(_context))
                {
                    AbortCurrentAction();
                    if (AutoReplan) TryStartNewPlan();
                }
            }
        }

        private void TryStartNewPlan()
        {
            if (_context == null) return;
            SetState(GOAPExecState.Planning);
            var goal = _planner.PickGoal(_goals, _context);
            if (goal != _currentGoal)
            {
                _currentGoal = goal;
                OnGoalChanged?.Invoke(goal);
            }
            if (goal == null)
            {
                SetState(GOAPExecState.Idle);
                return;
            }
            var plan = _planner.Plan(goal, _context);
            OnPlanChanged?.Invoke(plan);
            if (plan == null || plan.Count == 0)
            {
                SetState(GOAPExecState.Failed);
                _replanTimer = ReplanCooldown;
                return;
            }
            _plan = new Queue<GOAPActionV2>(plan);
            _replanAttempts = 0;
            StartNextAction();
        }

        private void StartNextAction()
        {
            if (_plan == null || _plan.Count == 0)
            {
                SetState(GOAPExecState.Completed);
                // 完成后回到 idle 重新规划
                SetState(GOAPExecState.Idle);
                return;
            }
            _currentAction = _plan.Dequeue();
            if (!_currentAction.CanExecute(_context!))
            {
                AbortCurrentAction();
                if (AutoReplan) TryStartNewPlan();
                return;
            }
            _actionTimer = _currentAction.EstimatedDuration;
            OnActionStarted?.Invoke(_currentAction);
            SetState(GOAPExecState.Executing);
        }

        private void UpdateExecuting(float dt)
        {
            if (_currentAction == null || _context == null) return;
            _actionTimer -= dt;
            bool success = _currentAction.Execute(_context);
            if (!success)
            {
                OnActionFinished?.Invoke(_currentAction, false);
                AbortCurrentAction();
                if (AutoReplan) TryStartNewPlan();
                return;
            }
            // 简化：定时器到 0 视为完成
            // 完整实现应该用 Executor 返回值或回调判断完成
            if (_actionTimer <= 0)
            {
                // 应用 effects 到世界状态
                _context.WorldState = _context.WorldState.ApplyEffects(_currentAction.Effects);
                OnActionFinished?.Invoke(_currentAction, true);
                _currentAction = null;
                StartNextAction();
            }
        }

        private void AbortCurrentAction()
        {
            if (_currentAction != null)
            {
                if (_currentAction.Interruptible)
                {
                    _currentAction = null;
                }
            }
            if (_plan != null) _plan.Clear();
            SetState(GOAPExecState.Aborted);
        }

        /// <summary>外部强制中止当前 plan。</summary>
        public void Abort()
        {
            AbortCurrentAction();
            SetState(GOAPExecState.Idle);
        }

        /// <summary>外部强制重规划。</summary>
        public void ForceReplan()
        {
            AbortCurrentAction();
            _replanAttempts = 0;
            TryStartNewPlan();
        }

        private void SetState(GOAPExecState newState)
        {
            if (_state == newState) return;
            _state = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    // ========================================================================
    //  七、GOAP 记忆
    // ========================================================================

    /// <summary>
    /// GOAP 短期记忆。缓存最近规划结果，避免每帧重规划。
    /// </summary>
    public sealed class GOAPMemory
    {
        private readonly Dictionary<string, (List<GOAPActionV2> plan, float time)> _planCache = new();
        private readonly Dictionary<string, float> _lastSeenTime = new();

        public float PlanCacheTTL = 2f; // 缓存有效期

        /// <summary>尝试获取缓存的 plan。</summary>
        public List<GOAPActionV2>? TryGetCachedPlan(string goalKey, float currentTime)
        {
            if (_planCache.TryGetValue(goalKey, out var entry))
            {
                if (currentTime - entry.time < PlanCacheTTL)
                    return entry.plan;
                _planCache.Remove(goalKey);
            }
            return null;
        }

        /// <summary>缓存 plan。</summary>
        public void CachePlan(string goalKey, List<GOAPActionV2> plan, float currentTime)
        {
            _planCache[goalKey] = (plan, currentTime);
        }

        /// <summary>记录最后看到某目标的时间。</summary>
        public void RecordSeen(string targetKey, float time) => _lastSeenTime[targetKey] = time;

        /// <summary>多久没看到某目标了。</summary>
        public float TimeSinceSeen(string targetKey, float currentTime)
            => _lastSeenTime.TryGetValue(targetKey, out var t) ? currentTime - t : float.MaxValue;

        public void Clear()
        {
            _planCache.Clear();
            _lastSeenTime.Clear();
        }
    }

    // ========================================================================
    //  八、Domain Builder (流式 API)
    // ========================================================================

    /// <summary>
    /// 流式 API 构建 GOAP domain。
    /// </summary>
    public sealed class GOAPDomainBuilder
    {
        private readonly GOAPPlannerV2 _planner;
        private readonly List<GOAPGoalV2> _goals = new();

        public GOAPDomainBuilder() { _planner = new GOAPPlannerV2(); }

        public GOAPDomainBuilder Action(string name, float cost,
            Action<WorldState> preconditions,
            Action<WorldState> effects,
            Func<GOAPContext, bool>? validator = null,
            Func<GOAPContext, float>? costMult = null)
        {
            var a = new GOAPActionV2(name) { BaseCost = cost };
            preconditions(a.Preconditions);
            effects(a.Effects);
            a.Validator = validator;
            a.CostMultiplier = costMult;
            _planner.RegisterAction(a);
            return this;
        }

        public GOAPDomainBuilder Goal(string name, float priority, Action<WorldState> desired)
        {
            var g = new GOAPGoalV2(name) { Priority = priority };
            desired(g.DesiredState);
            _goals.Add(g);
            return this;
        }

        public GOAPDomainBuilder GoalDynamic(string name, Func<GOAPContext, float> priorityFn, Action<WorldState> desired)
        {
            var g = new GOAPGoalV2(name) { DynamicPriority = priorityFn };
            desired(g.DesiredState);
            _goals.Add(g);
            return this;
        }

        public (GOAPPlannerV2 planner, List<GOAPGoalV2> goals) Build()
        {
            return (_planner, _goals);
        }
    }

    // ========================================================================
    //  九、预设动作库(常见模板)
    // ========================================================================

    /// <summary>常见 GOAP 动作预设。</summary>
    public static class GOAPActionPresets
    {
        /// <summary>移动到目标附近。</summary>
        public static GOAPActionV2 MoveTo(string targetKey, float range = 1.5f)
        {
            var a = new GOAPActionV2($"MoveTo({targetKey})")
            {
                BaseCost = 2f,
                EstimatedDuration = 2f
            };
            a.Validator = ctx =>
            {
                // 简化：假设有移动系统能到达
                return true;
            };
            a.Effects.SetBool($"near_{targetKey}", true);
            return a;
        }

        /// <summary>攻击目标。</summary>
        public static GOAPActionV2 Attack(string targetKey)
        {
            var a = new GOAPActionV2($"Attack({targetKey})")
            {
                BaseCost = 1f,
                EstimatedDuration = 0.5f
            };
            a.Preconditions.SetBool($"near_{targetKey}", true);
            a.Preconditions.SetBool("has_weapon", true);
            a.Effects.SetBool($"target_dead", true);
            a.Effects.SetFloat("ammo", -1f); // 消耗一发弹药
            return a;
        }

        /// <summary>拾取物品。</summary>
        public static GOAPActionV2 Pickup(string itemKey)
        {
            var a = new GOAPActionV2($"Pickup({itemKey})")
            {
                BaseCost = 1f,
                EstimatedDuration = 0.5f
            };
            a.Preconditions.SetBool($"near_{itemKey}", true);
            a.Effects.SetBool($"has_{itemKey}", true);
            return a;
        }

        /// <summary>开门。</summary>
        public static GOAPActionV2 OpenDoor(string doorKey)
        {
            var a = new GOAPActionV2($"OpenDoor({doorKey})")
            {
                BaseCost = 0.5f,
                EstimatedDuration = 1f
            };
            a.Preconditions.SetBool($"near_{doorKey}", true);
            a.Preconditions.SetBool($"door_locked", false);
            a.Effects.SetBool($"door_open", true);
            return a;
        }

        /// <summary>装弹。</summary>
        public static GOAPActionV2 Reload()
        {
            var a = new GOAPActionV2("Reload")
            {
                BaseCost = 1.5f,
                EstimatedDuration = 2f
            };
            a.Preconditions.SetBool("has_ammo_reserve", true);
            a.Effects.SetFloat("ammo", 30f);
            return a;
        }

        /// <summary>逃跑。</summary>
        public static GOAPActionV2 Flee(string threatKey)
        {
            var a = new GOAPActionV2($"Flee({threatKey})")
            {
                BaseCost = 3f,
                EstimatedDuration = 1f
            };
            a.Preconditions.SetBool($"threat_visible", true);
            a.Effects.SetBool("safe", true);
            return a;
        }
    }
}
