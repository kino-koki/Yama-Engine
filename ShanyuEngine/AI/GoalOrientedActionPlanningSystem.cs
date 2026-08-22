// ============================================================================
//  ShanyuEngine.AI.GoalOrientedActionPlanningSystem.cs
//  山雨引擎 -- GOAP 目标导向行动规划系统
//  对齐 UE GOAP / 经典 GOAP 设计模式
// ----------------------------------------------------------------------------
//  完整的 GOAP 系统，包含：
//    1. GOAPState              - 世界状态 (键值对集合，布尔/枚举/数值)
//    2. GOAPAction            - 行动 (名称/前提条件/效果/成本/执行/中断)
//    3. GOAPGoal              - 目标 (期望世界状态/优先级/可中断)
//    4. GOAPPlanner           - A* 规划器 (当前状态到目标状态)
//    5. GOAPAgent             - Agent (可用行动/当前计划/世界状态感知)
//    6. 内置行动模板          - MoveTo/Attack/PickupItem/DropItem/UseItem
//                                 OpenDoor/Hide/Flee/Heal/Reload
//    7. 规划缓存              - 相同状态 -> 缓存计划避免重复计算
//    8. 多目标优先级          - 同时追踪多个目标/冲突消解
//    9. 动态重规划            - 世界状态变化 -> 重新评估计划
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、世界状态 (GOAPState)
    // ========================================================================

    /// <summary>
    /// 世界状态值类型。
    /// </summary>
    public enum GOAPValueType
    {
        Bool,
        Int,
        Float,
        Enum,
        Vector3
    }

    /// <summary>
    /// 世界状态键值对。
    /// </summary>
    public sealed class GOAPStateKey
    {
        public string Key { get; }
        public GOAPValueType ValueType { get; }
        public object DefaultValue { get; }

        public GOAPStateKey(string key, GOAPValueType valueType, object defaultValue = null)
        {
            Key = key;
            ValueType = valueType;
            DefaultValue = defaultValue;
        }

        public override bool Equals(object obj)
        {
            if (obj is GOAPStateKey other) return Key == other.Key;
            return false;
        }

        public override int GetHashCode() => Key.GetHashCode();
    }

    /// <summary>
    /// GOAP 世界状态。键值对集合，支持布尔/枚举/数值。
    /// </summary>
    public sealed class GOAPState
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        /// <summary>获取值。</summary>
        public T GetValue<T>(string key)
        {
            if (_values.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
            }
            return default;
        }

        /// <summary>设置值。</summary>
        public void SetValue(string key, object value)
        {
            _values[key] = value;
        }

        /// <summary>是否有键。</summary>
        public bool HasKey(string key) => _values.ContainsKey(key);

        /// <summary>移除键。</summary>
        public void RemoveKey(string key) => _values.Remove(key);

        /// <summary>获取所有键。</summary>
        public IEnumerable<string> GetKeys() => _values.Keys;

        /// <summary>检查条件是否满足（键值相等）。</summary>
        public bool MeetsCondition(string key, object value)
        {
            if (!_values.TryGetValue(key, out var current)) return false;
            return Equals(current, value);
        }

        /// <summary>检查多个条件。</summary>
        public bool MeetsAllConditions(Dictionary<string, object> conditions)
        {
            if (conditions == null) return true;
            foreach (var kv in conditions)
            {
                if (!MeetsCondition(kv.Key, kv.Value)) return false;
            }
            return true;
        }

        /// <summary>应用效果（设置多个键值）。</summary>
        public void ApplyEffects(Dictionary<string, object> effects)
        {
            if (effects == null) return;
            foreach (var kv in effects)
                SetValue(kv.Key, kv.Value);
        }

        /// <summary>获取状态哈希（用于缓存）。</summary>
        public int GetStateHash()
        {
            int hash = 17;
            foreach (var kv in _values.OrderBy(kv => kv.Key))
            {
                hash = hash * 31 + kv.Key.GetHashCode();
                hash = hash * 31 + (kv.Value?.GetHashCode() ?? 0);
            }
            return hash;
        }

        /// <summary>克隆状态。</summary>
        public GOAPState Clone()
        {
            var clone = new GOAPState();
            foreach (var kv in _values)
                clone.SetValue(kv.Key, kv.Value);
            return clone;
        }

        /// <summary>序列化为字典。</summary>
        public Dictionary<string, object> Serialize() => new Dictionary<string, object>(_values);

        /// <summary>从字典反序列化。</summary>
        public void Deserialize(Dictionary<string, object> data)
        {
            _values.Clear();
            if (data != null)
            {
                foreach (var kv in data)
                    _values[kv.Key] = kv.Value;
            }
        }
    }

    // ========================================================================
    //  二、GOAP 行动 (GOAPAction)
    // ========================================================================

    /// <summary>
    /// GOAP 行动。定义前提条件、效果、成本和执行逻辑。
    /// </summary>
    public abstract class GOAPAction
    {
        public string ActionName { get; protected set; } = "UnnamedAction";
        public float Cost { get; set; } = 1f;

        /// <summary>前提条件集合（必须全部满足才能执行）。</summary>
        public Dictionary<string, object> Preconditions { get; } = new Dictionary<string, object>();

        /// <summary>效果集合（执行后对世界状态的改变）。</summary>
        public Dictionary<string, object> Effects { get; } = new Dictionary<string, object>();

        /// <summary>目标 Actor（可选）。</summary>
        public ShanyuEngine.GameFramework.Actor TargetActor { get; set; }

        /// <summary>检查是否在范围内（用于移动到目标后再执行）。</summary>
        public virtual bool IsInRange(GOAPAgent agent) => true;

        /// <summary>检查是否可用。</summary>
        public virtual bool IsAvailable(GOAPAgent agent) => true;

        /// <summary>执行行动。返回true表示成功完成。</summary>
        public abstract bool Execute(GOAPAgent agent, float deltaTime);

        /// <summary>中断执行。</summary>
        public virtual void Abort(GOAPAgent agent) { }

        /// <summary>重置行动状态（用于重新执行）。</summary>
        public virtual void Reset() { }

        /// <summary>添加前提条件。</summary>
        public GOAPAction AddPrecondition(string key, object value)
        {
            Preconditions[key] = value;
            return this;
        }

        /// <summary>添加效果。</summary>
        public GOAPAction AddEffect(string key, object value)
        {
            Effects[key] = value;
            return this;
        }
    }

    // ========================================================================
    //  三、内置 GOAP 行动模板
    // ========================================================================

    /// <summary>移动到目标位置。</summary>
    public sealed class GOAPAction_MoveTo : GOAPAction
    {
        public Vector3 TargetPosition { get; set; }
        public float AcceptRadius { get; set; } = 1f;
        private bool _started = false;

        public GOAPAction_MoveTo()
        {
            ActionName = "MoveTo";
            Cost = 0.5f;
        }

        public override void Reset()
        {
            _started = false;
            base.Reset();
        }

        public override bool IsInRange(GOAPAgent agent)
        {
            if (!TargetPosition.Equals(Vector3.Zero))
                return Vector3.Distance(agent.OwnerPosition, TargetPosition) <= AcceptRadius;
            return true;
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            if (!_started)
            {
                _started = true;
                agent.MoveTo?.Invoke(TargetPosition);
            }
            return Vector3.Distance(agent.OwnerPosition, TargetPosition) <= AcceptRadius;
        }
    }

    /// <summary>攻击目标。</summary>
    public sealed class GOAPAction_Attack : GOAPAction
    {
        public float AttackRange { get; set; } = 5f;
        public float DamageAmount { get; set; } = 10f;
        private float _cooldown = 0f;
        private const float CooldownTime = 1f;

        public GOAPAction_Attack()
        {
            ActionName = "Attack";
            Cost = 2f;
            AddPrecondition("HasWeapon", true);
            AddPrecondition("TargetVisible", true);
            AddEffect("TargetAlive", false);
            AddEffect("TargetHealth", 0);
        }

        public override void Reset()
        {
            _cooldown = 0f;
            base.Reset();
        }

        public override bool IsInRange(GOAPAgent agent)
        {
            if (TargetActor != null)
                return Vector3.Distance(agent.OwnerPosition, TargetActor.Position) <= AttackRange;
            return true;
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            _cooldown += deltaTime;
            if (_cooldown >= CooldownTime)
            {
                agent.PerformAttack?.Invoke(TargetActor);
                _cooldown = 0f;
                return true;
            }
            return false;
        }
    }

    /// <summary>拾取物品。</summary>
    public sealed class GOAPAction_PickupItem : GOAPAction
    {
        public string ItemName { get; set; } = "";

        public GOAPAction_PickupItem()
        {
            ActionName = "PickupItem";
            Cost = 0.3f;
            AddPrecondition("ItemReachable", true);
        }

        public override void Reset() { base.Reset(); }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            agent.WorldState.SetValue("Has" + ItemName, true);
            agent.WorldState.SetValue("ItemReachable", false);
            agent.OnItemPickedUp?.Invoke(ItemName);
            return true;
        }
    }

    /// <summary>丢弃物品。</summary>
    public sealed class GOAPAction_DropItem : GOAPAction
    {
        public string ItemName { get; set; } = "";

        public GOAPAction_DropItem()
        {
            ActionName = "DropItem";
            Cost = 0.2f;
            AddPrecondition("Has" + ItemName, true);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            agent.WorldState.SetValue("Has" + ItemName, false);
            return true;
        }
    }

    /// <summary>使用物品。</summary>
    public sealed class GOAPAction_UseItem : GOAPAction
    {
        public string ItemName { get; set; } = "";
        public string EffectKey { get; set; } = "";
        public object EffectValue { get; set; }

        public GOAPAction_UseItem()
        {
            ActionName = "UseItem";
            Cost = 0.5f;
            AddPrecondition("Has" + ItemName, true);
        }

        public override void Reset() { base.Reset(); }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            if (!string.IsNullOrEmpty(EffectKey))
                agent.WorldState.SetValue(EffectKey, EffectValue);
            agent.WorldState.SetValue("Has" + ItemName, false);
            agent.OnItemUsed?.Invoke(ItemName);
            return true;
        }
    }

    /// <summary>开门。</summary>
    public sealed class GOAPAction_OpenDoor : GOAPAction
    {
        public GOAPAction_OpenDoor()
        {
            ActionName = "OpenDoor";
            Cost = 0.3f;
            AddPrecondition("DoorClosed", true);
            AddPrecondition("DoorReachable", true);
            AddEffect("DoorClosed", false);
            AddEffect("DoorOpen", true);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            agent.OnDoorInteracted?.Invoke(true);
            return true;
        }
    }

    /// <summary>躲藏。</summary>
    public sealed class GOAPAction_Hide : GOAPAction
    {
        public GOAPAction_Hide()
        {
            ActionName = "Hide";
            Cost = 0.4f;
            AddPrecondition("CoverAvailable", true);
            AddEffect("IsHidden", true);
            AddEffect("TargetVisible", false);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            return true;
        }
    }

    /// <summary>逃跑。</summary>
    public sealed class GOAPAction_Flee : GOAPAction
    {
        public float FleeDistance { get; set; } = 50f;

        public GOAPAction_Flee()
        {
            ActionName = "Flee";
            Cost = 1.5f;
            AddPrecondition("ThreatPresent", true);
            AddEffect("ThreatPresent", false);
            AddEffect("IsSafe", true);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            var threat = agent.WorldState.GetValue<ShanyuEngine.GameFramework.Actor>("ThreatActor");
            if (threat != null)
            {
                var dir = agent.OwnerPosition - threat.Position;
                if (dir.LengthSquared() > 0.001f)
                {
                    dir = Vector3.Normalize(dir);
                    agent.MoveTo?.Invoke(agent.OwnerPosition + dir * FleeDistance);
                }
            }
            return true;
        }
    }

    /// <summary>治疗。</summary>
    public sealed class GOAPAction_Heal : GOAPAction
    {
        public float HealAmount { get; set; } = 30f;

        public GOAPAction_Heal()
        {
            ActionName = "Heal";
            Cost = 1f;
            AddPrecondition("HasMedkit", true);
            AddPrecondition("IsInjured", true);
            AddEffect("IsInjured", false);
            AddEffect("Health", 100);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            agent.OnHealed?.Invoke(HealAmount);
            return true;
        }
    }

    /// <summary>装弹。</summary>
    public sealed class GOAPAction_Reload : GOAPAction
    {
        public GOAPAction_Reload()
        {
            ActionName = "Reload";
            Cost = 0.8f;
            AddPrecondition("HasAmmo", false);
            AddPrecondition("HasAmmoClip", true);
            AddEffect("HasAmmo", true);
            AddEffect("HasAmmoClip", false);
        }

        public override bool Execute(GOAPAgent agent, float deltaTime)
        {
            agent.OnReloaded?.Invoke();
            return true;
        }
    }

    // ========================================================================
    //  四、GOAP 目标 (GOAPGoal)
    // ========================================================================

    /// <summary>
    /// GOAP 目标。定义期望的世界状态和优先级。
    /// </summary>
    public sealed class GOAPGoal
    {
        public string GoalName { get; set; } = "UnnamedGoal";
        public Dictionary<string, object> DesiredState { get; } = new Dictionary<string, object>();
        public float Priority { get; set; } = 1f;
        public bool CanBeInterrupted { get; set; } = true;

        /// <summary>目标是否已达成。</summary>
        public bool IsSatisfied(GOAPState worldState)
        {
            return worldState.MeetsAllConditions(DesiredState);
        }

        /// <summary>添加期望状态。</summary>
        public GOAPGoal AddDesiredState(string key, object value)
        {
            DesiredState[key] = value;
            return this;
        }
    }

    // ========================================================================
    //  五、GOAP 规划器 (GOAPPlanner)
    // ========================================================================

    /// <summary>
    /// GOAP A* 规划器。从当前世界状态搜索到目标状态的最优行动序列。
    /// </summary>
    public sealed class GOAPPlanner
    {
        /// <summary>最大规划节点数（防止无限搜索）。</summary>
        public int MaxNodesToSearch { get; set; } = 1000;

        /// <summary>规划超时（毫秒）。</summary>
        public int PlanningTimeoutMs { get; set; } = 100;

        /// <summary>规划统计。</summary>
        public int LastNodesSearched { get; private set; }
        public float LastPlanningTimeMs { get; private set; }

        /// <summary>规划行动序列。返回 null 表示无法规划。</summary>
        public Queue<GOAPAction> Plan(GOAPState worldState, List<GOAPAction> availableActions,
            Dictionary<string, object> goalState)
        {
            if (worldState == null || availableActions == null || goalState == null)
                return null;

            // 检查目标是否已满足
            if (worldState.MeetsAllConditions(goalState))
                return new Queue<GOAPAction>();

            var startTime = System.Diagnostics.Stopwatch.GetTimestamp();
            LastNodesSearched = 0;

            // A*搜索
            var startNode = new PlannerNode(null, null, worldState.Clone(), 0f);
            var openSet = new List<PlannerNode> { startNode };
            var closedSet = new HashSet<int>();

            while (openSet.Count > 0)
            {
                // 检查超时
                if (LastNodesSearched >= MaxNodesToSearch) break;
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - startTime) / 10000f > PlanningTimeoutMs) break;

                // 找最小fCost
                int bestIdx = 0;
                for (int i = 1; i < openSet.Count; i++)
                {
                    if (openSet[i].FCost < openSet[bestIdx].FCost) bestIdx = i;
                }

                var current = openSet[bestIdx];
                openSet.RemoveAt(bestIdx);
                LastNodesSearched++;

                // 检查目标是否满足
                if (current.State.MeetsAllConditions(goalState))
                {
                    LastPlanningTimeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTime) / 10000f;
                    return ReconstructPlan(current);
                }

                int stateHash = current.State.GetStateHash();
                if (closedSet.Contains(stateHash)) continue;
                closedSet.Add(stateHash);

                // 扩展所有可用行动
                foreach (var action in availableActions)
                {
                    if (!current.State.MeetsAllConditions(action.Preconditions)) continue;

                    // 应用效果
                    var newState = current.State.Clone();
                    newState.ApplyEffects(action.Effects);

                    int newStateHash = newState.GetStateHash();
                    if (closedSet.Contains(newStateHash)) continue;

                    float heuristic = CalculateHeuristic(newState, goalState);
                    var neighbor = new PlannerNode(action, current, newState,
                        current.GCost + action.Cost);
                    neighbor.HCost = heuristic;

                    // 检查是否已在openSet中
                    bool found = false;
                    for (int i = 0; i < openSet.Count; i++)
                    {
                        if (openSet[i].State.GetStateHash() == newStateHash)
                        {
                            if (neighbor.GCost < openSet[i].GCost)
                                openSet[i] = neighbor;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        openSet.Add(neighbor);
                }
            }

            LastPlanningTimeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTime) / 10000f;
            return null; // 无法规划
        }

        private float CalculateHeuristic(GOAPState state, Dictionary<string, object> goalState)
        {
            int unmet = 0;
            foreach (var kv in goalState)
            {
                if (!state.MeetsCondition(kv.Key, kv.Value))
                    unmet++;
            }
            return unmet;
        }

        private Queue<GOAPAction> ReconstructPlan(PlannerNode goalNode)
        {
            var plan = new Stack<GOAPAction>();
            var node = goalNode;
            while (node.Parent != null)
            {
                var action = (GOAPAction)Activator.CreateInstance(node.Action.GetType());
                // 复制属性
                action.TargetActor = node.Action.TargetActor;
                plan.Push(action);
                node = node.Parent;
            }

            return new Queue<GOAPAction>(plan);
        }

        private sealed class PlannerNode
        {
            public GOAPAction Action { get; }
            public PlannerNode Parent { get; }
            public GOAPState State { get; }
            public float GCost { get; }
            public float HCost { get; set; }
            public float FCost => GCost + HCost;

            public PlannerNode(GOAPAction action, PlannerNode parent, GOAPState state, float gCost)
            {
                Action = action;
                Parent = parent;
                State = state;
                GCost = gCost;
            }
        }
    }

    // ========================================================================
    //  六、GOAP Agent (GOAPAgent)
    // ========================================================================

    /// <summary>
    /// GOAP Agent。拥有可用行动、当前计划、世界状态感知和重规划能力。
    /// </summary>
    public sealed class GOAPAgent
    {
        /// <summary>世界状态（感知得到）。</summary>
        public GOAPState WorldState { get; } = new GOAPState();

        /// <summary>可用行动列表。</summary>
        public List<GOAPAction> AvailableActions { get; } = new List<GOAPAction>();

        /// <summary>目标列表。</summary>
        public List<GOAPGoal> Goals { get; } = new List<GOAPGoal>();

        /// <summary>当前行动计划。</summary>
        public Queue<GOAPAction> CurrentPlan { get; private set; }

        /// <summary>当前正在执行的行动。</summary>
        public GOAPAction CurrentAction { get; private set; }

        /// <summary>当前活跃目标。</summary>
        public GOAPGoal ActiveGoal { get; private set; }

        /// <summary>规划器。</summary>
        public GOAPPlanner Planner { get; } = new GOAPPlanner();

        /// <summary>所有者位置。</summary>
        public Vector3 OwnerPosition { get; set; }

        /// <summary>移动回调。</summary>
        public Action<Vector3> MoveTo { get; set; }

        /// <summary>攻击回调。</summary>
        public Action<ShanyuEngine.GameFramework.Actor> PerformAttack { get; set; }

        /// <summary>物品拾取回调。</summary>
        public Action<string> OnItemPickedUp { get; set; }

        /// <summary>物品使用回调。</summary>
        public Action<string> OnItemUsed { get; set; }

        /// <summary>门交互回调。</summary>
        public Action<bool> OnDoorInteracted { get; set; }

        /// <summary>治疗回调。</summary>
        public Action<float> OnHealed { get; set; }

        /// <summary>装弹回调。</summary>
        public Action OnReloaded { get; set; }

        // ── 缓存 ──────────────────────────────────────────────────

        private readonly Dictionary<int, Queue<GOAPAction>> _planCache =
            new Dictionary<int, Queue<GOAPAction>>();

        /// <summary>是否启用规划缓存。</summary>
        public bool EnableCache { get; set; } = true;

        /// <summary>缓存最大数量。</summary>
        public int MaxCacheSize { get; set; } = 100;

        // ── 多目标 ────────────────────────────────────────────────

        /// <summary>是否启用多目标追踪。</summary>
        public bool EnableMultiGoal { get; set; } = false;

        // ── 动态重规划 ────────────────────────────────────────────

        /// <summary>是否启用动态重规划。</summary>
        public bool EnableDynamicReplanning { get; set; } = true;

        /// <summary>重规划间隔（秒）。</summary>
        public float ReplanInterval { get; set; } = 2f;
        private float _replanTimer = 0f;

        /// <summary>上一次的世界状态哈希（用于检测变化）。</summary>
        private int _lastWorldStateHash = 0;

        // ── 行动管理 ────────────────────────────────────────────

        /// <summary>注册行动。</summary>
        public void RegisterAction(GOAPAction action)
        {
            if (action != null && !AvailableActions.Contains(action))
                AvailableActions.Add(action);
        }

        /// <summary>注销行动。</summary>
        public void UnregisterAction(GOAPAction action)
        {
            AvailableActions.Remove(action);
        }

        /// <summary>添加目标。</summary>
        public void AddGoal(GOAPGoal goal)
        {
            if (goal != null && !Goals.Contains(goal))
                Goals.Add(goal);
        }

        /// <summary>移除目标。</summary>
        public void RemoveGoal(GOAPGoal goal)
        {
            Goals.Remove(goal);
        }

        // ── 规划 ────────────────────────────────────────────────

        /// <summary>请求规划。选择最佳目标并生成行动计划。</summary>
        public bool RequestPlan()
        {
            // 选择最佳目标
            var bestGoal = SelectBestGoal();
            if (bestGoal == null) return false;

            // 检查缓存
            int cacheKey = WorldState.GetStateHash() ^ bestGoal.DesiredState.GetHashCode();
            if (EnableCache && _planCache.TryGetValue(cacheKey, out var cachedPlan))
            {
                CurrentPlan = cachedPlan;
                ActiveGoal = bestGoal;
                return true;
            }

            // 执行规划
            var plan = Planner.Plan(WorldState, AvailableActions, bestGoal.DesiredState);
            if (plan == null) return false;

            // 缓存结果
            if (EnableCache)
            {
                if (_planCache.Count >= MaxCacheSize)
                    _planCache.Clear();
                _planCache[cacheKey] = plan;
            }

            CurrentPlan = plan;
            ActiveGoal = bestGoal;
            _lastWorldStateHash = WorldState.GetStateHash();
            return true;
        }

        /// <summary>选择最高优先级的未满足目标。</summary>
        private GOAPGoal SelectBestGoal()
        {
            GOAPGoal best = null;
            float bestPriority = float.MinValue;

            foreach (var goal in Goals)
            {
                if (goal.IsSatisfied(WorldState)) continue;
                if (goal.Priority > bestPriority)
                {
                    bestPriority = goal.Priority;
                    best = goal;
                }
            }
            return best;
        }

        // ── 更新 ────────────────────────────────────────────────

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            // 检查是否需要动态重规划
            if (EnableDynamicReplanning)
            {
                _replanTimer += deltaTime;
                int currentHash = WorldState.GetStateHash();
                bool worldChanged = currentHash != _lastWorldStateHash;

                if (worldChanged || _replanTimer >= ReplanInterval)
                {
                    _replanTimer = 0f;
                    if (worldChanged && CurrentPlan != null)
                    {
                        RequestPlan();
                    }
                }
            }

            // 执行计划
            if (CurrentPlan == null || CurrentPlan.Count == 0)
            {
                RequestPlan();
                return;
            }

            // 检查当前行动
            if (CurrentAction == null)
            {
                CurrentAction = CurrentPlan.Dequeue();
                CurrentAction.Reset();

                // 如果行动不在范围内，需要先移动
                if (!CurrentAction.IsInRange(this))
                {
                    // 简化：直接执行（实际中应插入MoveTo行动）
                }
            }

            // 执行当前行动
            if (CurrentAction != null)
            {
                bool completed = CurrentAction.Execute(this, deltaTime);
                if (completed)
                {
                    CurrentAction = null;
                }
            }
        }

        /// <summary>中止当前行动。</summary>
        public void AbortCurrentAction()
        {
            if (CurrentAction != null)
            {
                CurrentAction.Abort(this);
                CurrentAction = null;
            }
        }

        /// <summary>清除计划。</summary>
        public void ClearPlan()
        {
            AbortCurrentAction();
            CurrentPlan = null;
            ActiveGoal = null;
        }

        /// <summary>清除缓存。</summary>
        public void ClearCache()
        {
            _planCache.Clear();
        }
    }
}
