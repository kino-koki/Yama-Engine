// ============================================================================
//  ShanyuEngine.AI.BehaviorTreeSystem.cs
//  山雨引擎 -- 行为树 AI 决策系统 (Behavior Tree System)
//  对齐 Unreal Engine Behavior Tree 框架
// ----------------------------------------------------------------------------
//  完整的行为树系统，包含：
//    1. BTNodeState            - 节点执行状态枚举 (Success/Failed/Running/Aborted/Inactive)
//    2. BTNode                 - 节点基类 (名称/状态/装饰器列表/服务列表/克隆)
//    3. BTComposite            - 复合节点
//       - Sequence / Selector / SimpleParallel / ConditionalBranch
//       - ForLoop / WhileLoop / Switch / WeightedSelector / Parallel
//    4. BTDecorator             - 装饰器节点
//       - BlackboardCondition / LoopDecorator / TimeLimit / Cooldown
//       - ForceSuccess / ForceFailure / KeepRunning / UntilFail / Repeat / Inverter
//    5. BTTask                  - 任务节点（叶节点）
//       - BTTask_MoveTo / BTTask_Wait / BTTask_PlayAnim / BTTask_WaitBlackboardTime
//       - BTTask_SetBlackboardValue / BTTask_ClearBlackboardValue / BTTask_TurnTo
//       - BTTask_RunBehaviorTree / BTTask_ActorAction / BTTask_SpawnActor
//       - BTTask_RandomSelector / BTTask_Log
//    6. BTService               - 服务节点 (Tick/启动/停止回调)
//       - BTService_UpdateBlackboard / BTService_Default / BTService_Custom
//    7. Blackboard              - 黑板 (Key-Value 存储)
//    8. BlackboardKeySelector<T>- 类型安全的黑板键选择器
//    9. BlackboardComponent     - 黑板组件 (附着到Pawn)
//   10. BehaviorTree           - 行为树资产 (根节点/黑板资产/Update频率)
//   11. BehaviorTreeInstance   - 运行时实例 (节点栈/黑板实例/执行状态/暂停/恢复/重启)
//   12. BehaviorTreeManager    - 全局管理器 (树资产注册/实例池/TickAll)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、枚举定义
    // ========================================================================

    /// <summary>
    /// 行为树节点执行状态。
    /// </summary>
    public enum BTNodeState
    {
        /// <summary>执行成功。</summary>
        Success,
        /// <summary>执行失败。</summary>
        Failed,
        /// <summary>正在执行中（需要后续 Tick）。</summary>
        Running,
        /// <summary>被中止（父节点或装饰器强制中止）。</summary>
        Aborted,
        /// <summary>未激活/未开始。</summary>
        Inactive
    }

    /// <summary>
    /// 中断条件。描述当条件变化时，正在运行的子树如何被中断。
    /// </summary>
    public enum BTAbortCondition
    {
        None,
        LowerPriority,
        Self,
        Both
    }

    /// <summary>
    /// 并行节点的完成策略。
    /// </summary>
    public enum BTParallelMode
    {
        WaitForAll,
        WaitForOne
    }

    /// <summary>
    /// 黑板键值类型。
    /// </summary>
    public enum BlackboardKeyType
    {
        None = 0,
        Int = 1,
        Float = 2,
        Bool = 3,
        String = 4,
        Vector3 = 5,
        Rotator = 6,
        Object = 7,
        Enum = 8,
        Class = 9,
        Struct = 10
    }

    // ========================================================================
    //  二、黑板系统 (Blackboard)
    // ========================================================================

    /// <summary>
    /// 黑板键定义。
    /// </summary>
    public sealed class BTBlackboardKeyDef
    {
        public string Name { get; set; } = "";
        public BlackboardKeyType KeyType { get; set; } = BlackboardKeyType.None;
        public object DefaultValue { get; set; }
        public int Index { get; set; } = -1;
        public List<Action<object, object>> Observers { get; } = new List<Action<object, object>>();

        public BTBlackboardKeyDef() { }
        public BTBlackboardKeyDef(string name, BlackboardKeyType type, object defaultValue = null)
        {
            Name = name;
            KeyType = type;
            DefaultValue = defaultValue;
        }
    }

    /// <summary>
    /// 类型安全的黑板键选择器。
    /// </summary>
    public sealed class BlackboardKeySelector<T>
    {
        public string SelectedKeyName { get; set; } = "";
        public int CachedIndex { get; set; } = -1;
        public bool IsResolved => CachedIndex >= 0;

        public T GetValue(Blackboard bb)
        {
            if (bb == null || !IsResolved) return default;
            return bb.GetValue<T>(CachedIndex);
        }

        public void SetValue(Blackboard bb, T value)
        {
            if (bb == null || !IsResolved) return;
            bb.SetValue(CachedIndex, value);
        }

        public bool ResolveKey(BTBlackboardDefinition def)
        {
            if (def == null) return false;
            CachedIndex = def.GetKeyIndex(SelectedKeyName);
            return IsResolved;
        }
    }

    /// <summary>
    /// 黑板键定义集合。
    /// </summary>
    public sealed class BTBlackboardDefinition
    {
        private readonly Dictionary<string, BTBlackboardKeyDef> _keys = new Dictionary<string, BTBlackboardKeyDef>();
        private readonly List<BTBlackboardKeyDef> _orderedKeys = new List<BTBlackboardKeyDef>();

        public IReadOnlyList<BTBlackboardKeyDef> Keys => _orderedKeys;

        public void AddKey(BTBlackboardKeyDef key)
        {
            if (string.IsNullOrEmpty(key.Name)) return;
            if (_keys.ContainsKey(key.Name)) return;
            key.Index = _orderedKeys.Count;
            _keys[key.Name] = key;
            _orderedKeys.Add(key);
        }

        public BTBlackboardKeyDef AddKey(string name, BlackboardKeyType type, object defaultValue = null)
        {
            var key = new BTBlackboardKeyDef(name, type, defaultValue);
            AddKey(key);
            return key;
        }

        public int GetKeyIndex(string name)
        {
            if (_keys.TryGetValue(name, out var key)) return key.Index;
            return -1;
        }

        public BTBlackboardKeyDef GetKey(string name)
        {
            _keys.TryGetValue(name, out var key);
            return key;
        }

        public Blackboard CreateBlackboard()
        {
            return new Blackboard(this);
        }

        public List<Dictionary<string, object>> SerializeKeys()
        {
            return _orderedKeys.Select(k => new Dictionary<string, object>
            {
                { "name", k.Name },
                { "type", (int)k.KeyType },
                { "default", k.DefaultValue }
            }).ToList();
        }
    }

    /// <summary>
    /// 黑板运行时实例。Key-Value 存储，支持对象引用/向量/布尔/字符串/枚举/类/结构体。
    /// </summary>
    public class Blackboard
    {
        private readonly BTBlackboardDefinition _definition;
        private readonly object[] _values;
        private readonly Dictionary<string, int> _dynamicKeys = new Dictionary<string, int>();
        private readonly List<(string Name, object Value)> _dynamicValues = new List<(string, object)>();

        public BTBlackboardDefinition Definition => _definition;

        internal Blackboard(BTBlackboardDefinition definition)
        {
            _definition = definition;
            _values = new object[definition.Keys.Count];
            for (int i = 0; i < definition.Keys.Count; i++)
                _values[i] = definition.Keys[i].DefaultValue;
        }

        public T GetValue<T>(int index)
        {
            if (index < 0 || index >= _values.Length) return default;
            if (_values[index] is T typed) return typed;
            return default;
        }

        public void SetValue(int index, object value)
        {
            if (index < 0 || index >= _values.Length) return;
            var old = _values[index];
            _values[index] = value;
            NotifyObservers(index, old, value);
        }

        public T GetValue<T>(string name)
        {
            int idx = _definition.GetKeyIndex(name);
            if (idx >= 0) return GetValue<T>(idx);
            return default;
        }

        public void SetValue(string name, object value)
        {
            int idx = _definition.GetKeyIndex(name);
            if (idx >= 0) SetValue(idx, value);
        }

        public void SetDynamicValue(string name, object value)
        {
            if (_dynamicKeys.TryGetValue(name, out int idx))
                _dynamicValues[idx] = (name, value);
            else
            {
                _dynamicKeys[name] = _dynamicValues.Count;
                _dynamicValues.Add((name, value));
            }
        }

        public T GetDynamicValue<T>(string name)
        {
            if (_dynamicKeys.TryGetValue(name, out int idx))
            {
                if (_dynamicValues[idx].Value is T typed) return typed;
            }
            return default;
        }

        public void AddObserver(string keyName, Action<object, object> observer)
        {
            var key = _definition.GetKey(keyName);
            if (key != null) key.Observers.Add(observer);
        }

        public void RemoveObserver(string keyName, Action<object, object> observer)
        {
            var key = _definition.GetKey(keyName);
            if (key != null) key.Observers.Remove(observer);
        }

        private void NotifyObservers(int index, object oldVal, object newVal)
        {
            if (index >= 0 && index < _definition.Keys.Count)
            {
                var key = _definition.Keys[index];
                foreach (var obs in key.Observers)
                {
                    try { obs(oldVal, newVal); } catch { }
                }
            }
        }

        public void ClearDynamicValues()
        {
            _dynamicKeys.Clear();
            _dynamicValues.Clear();
        }

        public void ClearValue(string name)
        {
            int idx = _definition.GetKeyIndex(name);
            if (idx >= 0)
            {
                var old = _values[idx];
                _values[idx] = null;
                NotifyObservers(idx, old, null);
            }
            if (_dynamicKeys.TryGetValue(name, out int didx))
            {
                _dynamicValues[didx] = (name, null);
            }
        }

        public bool HasKey(string name)
        {
            return _definition.GetKeyIndex(name) >= 0 || _dynamicKeys.ContainsKey(name);
        }

        public Dictionary<string, object> Serialize()
        {
            var dict = new Dictionary<string, object>();
            for (int i = 0; i < _definition.Keys.Count; i++)
            {
                if (_values[i] != null)
                    dict[_definition.Keys[i].Name] = _values[i];
            }
            foreach (var kv in _dynamicValues)
            {
                if (kv.Value != null)
                    dict["dyn:" + kv.Name] = kv.Value;
            }
            return dict;
        }

        public void Deserialize(Dictionary<string, object> dict)
        {
            foreach (var kv in dict)
            {
                if (kv.Key.StartsWith("dyn:"))
                    SetDynamicValue(kv.Key.Substring(4), kv.Value);
                else
                {
                    int idx = _definition.GetKeyIndex(kv.Key);
                    if (idx >= 0) SetValue(idx, kv.Value);
                }
            }
        }
    }

    /// <summary>
    /// 兼容别名，保持与旧代码的兼容。
    /// </summary>
    public sealed class BTBlackboard : Blackboard
    {
        internal BTBlackboard(BTBlackboardDefinition definition) : base(definition) { }
    }

    // ========================================================================
    //  三、BlackboardComponent - 黑板组件（附着到Pawn）
    // ========================================================================

    /// <summary>
    /// 黑板组件。附着到 Pawn 上，提供黑板实例管理。
    /// </summary>
    public class BlackboardComponent
    {
        /// <summary>黑板实例。</summary>
        public Blackboard Blackboard { get; private set; }

        /// <summary>所属 Pawn 的引用。</summary>
        public ShanyuEngine.GameFramework.Pawn OwnerPawn { get; private set; }

        /// <summary>黑板定义。</summary>
        public BTBlackboardDefinition Definition { get; private set; }

        public BlackboardComponent() { }

        public BlackboardComponent(BTBlackboardDefinition definition)
        {
            Definition = definition;
            Blackboard = definition.CreateBlackboard();
        }

        /// <summary>初始化黑板组件。</summary>
        public void Initialize(ShanyuEngine.GameFramework.Pawn owner, BTBlackboardDefinition definition)
        {
            OwnerPawn = owner;
            Definition = definition;
            Blackboard = definition.CreateBlackboard();
        }

        /// <summary>使用预创建的黑板初始化。</summary>
        public void Initialize(ShanyuEngine.GameFramework.Pawn owner, Blackboard sharedBlackboard)
        {
            OwnerPawn = owner;
            Blackboard = sharedBlackboard;
        }

        /// <summary>获取黑板值。</summary>
        public T GetValue<T>(string keyName)
        {
            return Blackboard?.GetValue<T>(keyName) ?? default;
        }

        /// <summary>设置黑板值。</summary>
        public void SetValue(string keyName, object value)
        {
            Blackboard?.SetValue(keyName, value);
        }

        /// <summary>清除黑板值。</summary>
        public void ClearValue(string keyName)
        {
            Blackboard?.ClearValue(keyName);
        }

        /// <summary>重置黑板到默认值。</summary>
        public void ResetBlackboard()
        {
            if (Definition != null)
                Blackboard = Definition.CreateBlackboard();
        }
    }

    // ========================================================================
    //  四、BTNode 基类
    // ========================================================================

    /// <summary>
    /// 行为树节点上下文。
    /// </summary>
    public sealed class BTNodeContext
    {
        public BehaviorTreeInstance TreeInstance { get; }
        public Blackboard Blackboard => TreeInstance?.Blackboard;
        public float DeltaTime { get; set; }
        public BTNode ActiveNode { get; set; }
        public object UserData { get; set; }
        public bool IsDebugMode { get; set; }

        public BTNodeContext(BehaviorTreeInstance inst)
        {
            TreeInstance = inst;
        }
    }

    /// <summary>
    /// 行为树节点基类。包含装饰器列表和服务列表。
    /// </summary>
    public abstract class BTNode
    {
        public string NodeName { get; set; } = "UnnamedNode";
        public string Description { get; set; } = "";
        public int NodeId { get; internal set; } = -1;
        public List<BTNode> Children { get; } = new List<BTNode>();
        public BTNode Parent { get; internal set; }
        public List<BTDecorator> Decorators { get; } = new List<BTDecorator>();
        public List<BTService> Services { get; } = new List<BTService>();
        public BTNodeState LastState { get; protected set; } = BTNodeState.Inactive;
        protected bool _initialized;
        public bool HasBreakpoint { get; set; }
        public int ExecutionCount { get; private set; }

        public virtual void InitNode(BTNodeContext ctx)
        {
            _initialized = true;
            foreach (var service in Services)
                service.StartService(ctx);
        }

        public abstract BTNodeState Execute(BTNodeContext ctx);

        public virtual void Abort(BTNodeContext ctx)
        {
            foreach (var service in Services)
                service.StopService(ctx);
            LastState = BTNodeState.Aborted;
            _initialized = false;
        }

        public virtual void Cleanup(BTNodeContext ctx)
        {
            foreach (var service in Services)
                service.StopService(ctx);
            _initialized = false;
        }

        public T AddChild<T>(T child) where T : BTNode
        {
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public T AddDecorator<T>(T decorator) where T : BTDecorator
        {
            decorator.SetDecoratedNode(this);
            Decorators.Add(decorator);
            return decorator;
        }

        public T AddService<T>(T service) where T : BTService
        {
            Services.Add(service);
            return service;
        }

        internal BTNodeState ExecuteWithDecorators(BTNodeContext ctx)
        {
            ExecutionCount++;
            ctx.ActiveNode = this;

            // Tick services
            foreach (var service in Services)
                service.TickService(ctx);

            if (HasBreakpoint && ctx.IsDebugMode)
                ctx.TreeInstance?.OnBreakpointHit?.Invoke(this, ctx);

            return ExecuteDecoratorChain(0, ctx);
        }

        private BTNodeState ExecuteDecoratorChain(int decoratorIndex, BTNodeContext ctx)
        {
            if (decoratorIndex >= Decorators.Count)
                return Execute(ctx);

            var decorator = Decorators[decoratorIndex];
            if (!decorator.ConditionCheck(ctx))
                return decorator.AbortResult;

            var nodeResult = ExecuteDecoratorChain(decoratorIndex + 1, ctx);
            return decorator.ApplyResult(nodeResult);
        }

        public virtual BTNode Clone()
        {
            var clone = (BTNode)Activator.CreateInstance(GetType());
            clone.NodeName = NodeName;
            clone.Description = Description;
            clone.HasBreakpoint = HasBreakpoint;
            foreach (var child in Children)
                clone.AddChild(child.Clone());
            foreach (var dec in Decorators)
                clone.AddDecorator(dec.Clone());
            foreach (var svc in Services)
                clone.AddService((BTService)svc.Clone());
            return clone;
        }
    }

    // ========================================================================
    //  五、复合节点 (BTComposite)
    // ========================================================================

    /// <summary>
    /// 复合节点基类。
    /// </summary>
    public abstract class BTComposite : BTNode
    {
        protected int _currentChildIndex = 0;
        public BTAbortCondition AbortCondition { get; set; } = BTAbortCondition.None;

        public override void Abort(BTNodeContext ctx)
        {
            if (_currentChildIndex >= 0 && _currentChildIndex < Children.Count)
                Children[_currentChildIndex].Abort(ctx);
            _currentChildIndex = 0;
            base.Abort(ctx);
        }

        public override void Cleanup(BTNodeContext ctx)
        {
            foreach (var child in Children) child.Cleanup(ctx);
            _currentChildIndex = 0;
            base.Cleanup(ctx);
        }

        protected void ResetChildren() { _currentChildIndex = 0; }
    }

    /// <summary>
    /// 顺序节点：依次执行所有子节点，任一失败则整体失败，全部成功则成功。
    /// </summary>

    /// <summary>
    /// 选择节点：依次尝试子节点，任一成功则成功，全部失败则失败。
    /// </summary>

    /// <summary>
    /// 简单并行节点：主任务 + 辅助任务。主任务完成时整体完成，辅助任务被中止。
    /// </summary>
    public sealed class BTSimpleParallel : BTComposite
    {
        public BTSimpleParallel() { NodeName = "SimpleParallel"; }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (Children.Count < 1) return BTNodeState.Failed;

            // 第一个子节点是主任务
            var mainResult = Children[0].ExecuteWithDecorators(ctx);
            Children[0].LastState = mainResult;

            // 辅助任务继续执行
            for (int i = 1; i < Children.Count; i++)
            {
                var bgResult = Children[i].ExecuteWithDecorators(ctx);
                Children[i].LastState = bgResult;
            }

            if (mainResult != BTNodeState.Running)
            {
                for (int i = 1; i < Children.Count; i++)
                    Children[i].Abort(ctx);
                _currentChildIndex = 0;
                return mainResult;
            }

            return BTNodeState.Running;
        }
    }

    /// <summary>
    /// 条件分支节点：根据条件选择执行分支。
    /// </summary>
    public sealed class BTConditionalBranch : BTComposite
    {
        public BTConditionalBranch() { NodeName = "ConditionalBranch"; }

        /// <summary>条件评估委托。</summary>
        public Func<BTNodeContext, bool> Condition { get; set; }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (Children.Count == 0) return BTNodeState.Failed;

            int branchIndex = 0;
            if (Condition != null && Condition(ctx))
                branchIndex = 1;

            if (branchIndex >= Children.Count) branchIndex = Children.Count - 1;

            var child = Children[branchIndex];
            var result = child.ExecuteWithDecorators(ctx);
            child.LastState = result;

            if (result != BTNodeState.Running)
                return result;
            return BTNodeState.Running;
        }
    }

    /// <summary>
    /// For循环节点：执行子节点指定次数。
    /// </summary>
    public sealed class BTForLoop : BTComposite
    {
        public BTForLoop() { NodeName = "ForLoop"; }

        /// <summary>循环次数黑板键名。</summary>
        public string LoopCountKey { get; set; } = "LoopCount";
        /// <summary>默认循环次数。</summary>
        public int DefaultLoopCount { get; set; } = 3;
        private int _currentIteration = 0;
        private int _totalIterations = 0;

        public override void InitNode(BTNodeContext ctx)
        {
            base.InitNode(ctx);
            _currentIteration = 0;
            if (ctx.Blackboard != null && !string.IsNullOrEmpty(LoopCountKey))
                _totalIterations = ctx.Blackboard.GetValue<int>(LoopCountKey);
            else
                _totalIterations = DefaultLoopCount;
        }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (!_initialized) InitNode(ctx);

            if (Children.Count == 0) return BTNodeState.Failed;
            if (_currentIteration >= _totalIterations)
            {
                _currentIteration = 0;
                return BTNodeState.Success;
            }

            var child = Children[0];
            var result = child.ExecuteWithDecorators(ctx);
            child.LastState = result;

            if (result == BTNodeState.Running) return BTNodeState.Running;
            if (result == BTNodeState.Success)
                _currentIteration++;
            else
            {
                _currentIteration = 0;
                return BTNodeState.Failed;
            }

            return BTNodeState.Running;
        }

        public override void Abort(BTNodeContext ctx)
        {
            _currentIteration = 0;
            base.Abort(ctx);
        }
    }

    /// <summary>
    /// While循环节点：条件满足时持续执行子节点。
    /// </summary>
    public sealed class BTWhileLoop : BTComposite
    {
        public BTWhileLoop() { NodeName = "WhileLoop"; }

        /// <summary>循环条件委托。</summary>
        public Func<BTNodeContext, bool> LoopCondition { get; set; }
        /// <summary>黑板键名作为条件（读取bool）。</summary>
        public string ConditionKey { get; set; } = "";

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (Children.Count == 0) return BTNodeState.Failed;

            bool shouldLoop = true;
            if (LoopCondition != null)
                shouldLoop = LoopCondition(ctx);
            else if (ctx.Blackboard != null && !string.IsNullOrEmpty(ConditionKey))
                shouldLoop = ctx.Blackboard.GetValue<bool>(ConditionKey);

            if (!shouldLoop)
            {
                if (_currentChildIndex < Children.Count)
                    Children[_currentChildIndex].Abort(ctx);
                _currentChildIndex = 0;
                return BTNodeState.Success;
            }

            var child = Children[0];
            var result = child.ExecuteWithDecorators(ctx);
            child.LastState = result;

            if (result == BTNodeState.Running) return BTNodeState.Running;
            if (result == BTNodeState.Failed)
            {
                _currentChildIndex = 0;
                return BTNodeState.Failed;
            }

            return BTNodeState.Running;
        }
    }

    /// <summary>
    /// Switch节点：根据黑板整数值选择执行哪个子节点。
    /// </summary>
    public sealed class BTSwitch : BTComposite
    {
        public string SwitchKey { get; set; } = "SwitchIndex";
        public int DefaultIndex { get; set; } = 0;

        public BTSwitch() { NodeName = "Switch"; }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (Children.Count == 0) return BTNodeState.Failed;

            int index = DefaultIndex;
            if (ctx.Blackboard != null && !string.IsNullOrEmpty(SwitchKey))
            {
                var val = ctx.Blackboard.GetValue<int>(SwitchKey);
                if (val >= 0 && val < Children.Count) index = val;
                else index = DefaultIndex;
            }

            var child = Children[index];
            var result = child.ExecuteWithDecorators(ctx);
            child.LastState = result;

            if (result != BTNodeState.Running) return result;
            return BTNodeState.Running;
        }
    }

    /// <summary>
    /// 并行节点：同时执行所有子节点。
    /// </summary>
    public sealed class BTParallel : BTComposite
    {
        public BTParallelMode Mode { get; set; } = BTParallelMode.WaitForAll;
        public bool AbortOnFail { get; set; } = true;

        public BTParallel() { NodeName = "Parallel"; }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            int successCount = 0, failCount = 0;

            foreach (var child in Children)
            {
                var result = child.ExecuteWithDecorators(ctx);
                child.LastState = result;

                switch (result)
                {
                    case BTNodeState.Success: successCount++; break;
                    case BTNodeState.Failed: failCount++; break;
                }

                if (AbortOnFail && failCount > 0 && Mode == BTParallelMode.WaitForAll)
                {
                    foreach (var c in Children) c.Abort(ctx);
                    _currentChildIndex = 0;
                    return BTNodeState.Failed;
                }
            }

            if (Mode == BTParallelMode.WaitForOne)
            {
                if (successCount > 0)
                {
                    foreach (var c in Children) c.Abort(ctx);
                    _currentChildIndex = 0;
                    return BTNodeState.Success;
                }
            }
            else
            {
                if (successCount == Children.Count)
                {
                    _currentChildIndex = 0;
                    return BTNodeState.Success;
                }
            }

            if (failCount == Children.Count)
            {
                _currentChildIndex = 0;
                return BTNodeState.Failed;
            }

            return BTNodeState.Running;
        }
    }

    /// <summary>
    /// 加权选择节点。
    /// </summary>
    public sealed class BTWeightedSelector : BTComposite
    {
        public List<float> Weights { get; } = new List<float>();
        private readonly Random _rng = new Random();

        public BTWeightedSelector() { NodeName = "WeightedSelector"; }

        public override BTNodeState Execute(BTNodeContext ctx)
        {
            if (Children.Count == 0) return BTNodeState.Failed;
            while (Weights.Count < Children.Count) Weights.Add(1f);

            if (!_initialized)
            {
                _currentChildIndex = SelectByWeight();
                InitNode(ctx);
            }

            var child = Children[_currentChildIndex];
            var result = child.ExecuteWithDecorators(ctx);
            child.LastState = result;

            if (result != BTNodeState.Running) { _currentChildIndex = 0; return result; }
            return BTNodeState.Running;
        }

        private int SelectByWeight()
        {
            float total = 0;
            for (int i = 0; i < Weights.Count && i < Children.Count; i++)
                total += Weights[i];
            float r = (float)_rng.NextDouble() * total;
            float cum = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                cum += (i < Weights.Count ? Weights[i] : 1f);
                if (r <= cum) return i;
            }
            return Children.Count - 1;
        }

        public override BTNode Clone()
        {
            var clone = (BTWeightedSelector)base.Clone();
            clone.Weights.Clear();
            clone.Weights.AddRange(Weights);
            return clone;
        }
    }

    // ========================================================================
    //  六、装饰器节点 (BTDecorator)
    // ========================================================================

    /// <summary>
    /// 装饰器节点基类。
    /// </summary>
    public abstract class BTDecorator : BTNode
    {
        private BTNode _decoratedNode;
        public BTNodeState AbortResult { get; protected set; } = BTNodeState.Failed;
        public BTAbortCondition AbortCondition { get; set; } = BTAbortCondition.None;

        public virtual bool ConditionCheck(BTNodeContext ctx) => true;
        public virtual BTNodeState ApplyResult(BTNodeState childResult) => childResult;

        internal void SetDecoratedNode(BTNode node) { _decoratedNode = node; }

        public override BTNodeState Execute(BTNodeContext ctx) => BTNodeState.Success;

        public override void Abort(BTNodeContext ctx)
        {
            _decoratedNode?.Abort(ctx);
            base.Abort(ctx);
        }

        public override void Cleanup(BTNodeContext ctx)
        {
            _decoratedNode?.Cleanup(ctx);
            base.Cleanup(ctx);
        }

        public override BTNode Clone()
        {
            var clone = (BTDecorator)Activator.CreateInstance(GetType());
            clone.NodeName = NodeName;
            clone.Description = Description;
            clone.AbortResult = AbortResult;
            clone.AbortCondition = AbortCondition;
            return clone;
        }
    }

    /// <summary>黑板条件装饰器。</summary>

    /// <summary>循环装饰器。</summary>
    public sealed class LoopDecorator : BTDecorator
    {
        public int LoopCount { get; set; } = 1;
        public bool InfiniteLoop { get; set; }
        private int _currentIteration = 0;

        public LoopDecorator() { NodeName = "LoopDecorator"; }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult == BTNodeState.Running) return childResult;
            _currentIteration++;
            if (!InfiniteLoop && _currentIteration >= LoopCount)
            {
                _currentIteration = 0;
                return BTNodeState.Success;
            }
            return BTNodeState.Running;
        }

        public override void Abort(BTNodeContext ctx) { _currentIteration = 0; base.Abort(ctx); }
    }

    /// <summary>时间限制装饰器。</summary>
    public sealed class TimeLimit : BTDecorator
    {
        public float MaxTime { get; set; } = 5f;
        public BTNodeState TimeoutResult { get; set; } = BTNodeState.Failed;
        private float _elapsedTime = 0f;

        public TimeLimit() { NodeName = "TimeLimit"; }

        public override bool ConditionCheck(BTNodeContext ctx)
        {
            _elapsedTime += ctx.DeltaTime;
            return _elapsedTime <= MaxTime;
        }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult != BTNodeState.Running) _elapsedTime = 0f;
            return childResult;
        }

        public override void Abort(BTNodeContext ctx) { _elapsedTime = 0f; base.Abort(ctx); }
    }

    /// <summary>冷却装饰器。</summary>
    public sealed class Cooldown : BTDecorator
    {
        public float CooldownTime { get; set; } = 1f;
        private float _remainingCooldown = 0f;

        public Cooldown() { NodeName = "Cooldown"; }

        public override bool ConditionCheck(BTNodeContext ctx)
        {
            if (_remainingCooldown > 0f)
            {
                _remainingCooldown -= ctx.DeltaTime;
                return false;
            }
            return true;
        }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult != BTNodeState.Running)
                _remainingCooldown = CooldownTime;
            return childResult;
        }

        public override void Abort(BTNodeContext ctx) { _remainingCooldown = 0f; base.Abort(ctx); }
    }

    /// <summary>强制成功装饰器。</summary>
    public sealed class ForceSuccess : BTDecorator
    {
        public ForceSuccess() { NodeName = "ForceSuccess"; }
        public override BTNodeState ApplyResult(BTNodeState childResult) => BTNodeState.Success;
    }

    /// <summary>强制失败装饰器。</summary>
    public sealed class ForceFailure : BTDecorator
    {
        public ForceFailure() { NodeName = "ForceFailure"; }
        public override BTNodeState ApplyResult(BTNodeState childResult) => BTNodeState.Failed;
    }

    /// <summary>保持运行装饰器：子节点结果非Running时保持Running直到条件不满足。</summary>
    public sealed class KeepRunning : BTDecorator
    {
        /// <summary>保持运行的条件。</summary>
        public Func<BTNodeContext, bool> KeepCondition { get; set; }

        public KeepRunning() { NodeName = "KeepRunning"; }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (KeepCondition != null && KeepCondition(default))
                return BTNodeState.Running;
            return childResult;
        }
    }

    /// <summary>直到失败装饰器：子节点成功时返回Running，失败时返回成功。</summary>
    public sealed class UntilFail : BTDecorator
    {
        public UntilFail() { NodeName = "UntilFail"; }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult == BTNodeState.Success) return BTNodeState.Running;
            if (childResult == BTNodeState.Failed) return BTNodeState.Success;
            return childResult;
        }
    }

    /// <summary>重复装饰器：子节点完成后重新执行。</summary>
    public sealed class Repeat : BTDecorator
    {
        public int Times { get; set; } = 1;
        private int _currentCount = 0;

        public Repeat() { NodeName = "Repeat"; }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult == BTNodeState.Running) return childResult;
            _currentCount++;
            if (_currentCount >= Times)
            {
                _currentCount = 0;
                return childResult;
            }
            return BTNodeState.Running;
        }

        public override void Abort(BTNodeContext ctx) { _currentCount = 0; base.Abort(ctx); }
    }

    /// <summary>反转子节点结果。</summary>
    public sealed class BTDecoratorInverter : BTDecorator
    {
        public BTDecoratorInverter() { NodeName = "Inverter"; }

        public override BTNodeState ApplyResult(BTNodeState childResult)
        {
            if (childResult == BTNodeState.Success) return BTNodeState.Failed;
            if (childResult == BTNodeState.Failed) return BTNodeState.Success;
            return childResult;
        }
    }

    // ========================================================================
    //  七、服务节点 (BTService)
    // ========================================================================

    /// <summary>
    /// 服务节点基类。定期在节点执行期间 Tick，提供持续更新能力。
    /// </summary>
    public abstract class BTService
    {
        public string ServiceName { get; set; } = "UnnamedService";
        public float TickInterval { get; set; } = 0.5f;
        private float _tickAccumulator = 0f;

        /// <summary>服务启动时调用。</summary>
        public virtual void StartService(BTNodeContext ctx) { _tickAccumulator = 0f; }

        /// <summary>服务停止时调用。</summary>
        public virtual void StopService(BTNodeContext ctx) { }

        /// <summary>服务 Tick。</summary>
        public virtual void TickService(BTNodeContext ctx)
        {
            _tickAccumulator += ctx.DeltaTime;
            if (_tickAccumulator >= TickInterval)
            {
                _tickAccumulator -= TickInterval;
                PerformService(ctx);
            }
        }

        /// <summary>实际服务逻辑（由子类实现）。</summary>
        protected virtual void PerformService(BTNodeContext ctx) { }

        /// <summary>克隆服务。</summary>
        public virtual BTService Clone()
        {
            var clone = (BTService)Activator.CreateInstance(GetType());
            clone.ServiceName = ServiceName;
            clone.TickInterval = TickInterval;
            return clone;
        }
    }

    /// <summary>更新黑板服务：定期更新黑板值。</summary>
    public sealed class BTService_UpdateBlackboard : BTService
    {
        public string TargetKey { get; set; } = "";
        public Func<BTNodeContext, object> UpdateFunc { get; set; }

        public BTService_UpdateBlackboard() { ServiceName = "UpdateBlackboard"; TickInterval = 0.5f; }

        protected override void PerformService(BTNodeContext ctx)
        {
            if (ctx.Blackboard == null || string.IsNullOrEmpty(TargetKey) || UpdateFunc == null) return;
            var value = UpdateFunc(ctx);
            ctx.Blackboard.SetDynamicValue(TargetKey, value);
        }
    }

    /// <summary>默认服务：提供基础 Tick。</summary>
    public sealed class BTService_Default : BTService
    {
        public BTService_Default() { ServiceName = "DefaultService"; TickInterval = 1f; }

        public Action<BTNodeContext> OnTick { get; set; }

        protected override void PerformService(BTNodeContext ctx)
        {
            OnTick?.Invoke(ctx);
        }
    }

    /// <summary>自定义服务：通过委托自定义。</summary>
    public sealed class BTService_Custom : BTService
    {
        public Action<BTNodeContext> OnStart { get; set; }
        public Action<BTNodeContext> OnTick { get; set; }
        public Action<BTNodeContext> OnStop { get; set; }

        public BTService_Custom() { ServiceName = "CustomService"; TickInterval = 0.5f; }

        public override void StartService(BTNodeContext ctx) { OnStart?.Invoke(ctx); }
        public override void StopService(BTNodeContext ctx) { OnStop?.Invoke(ctx); }

        protected override void PerformService(BTNodeContext ctx)
        {
            OnTick?.Invoke(ctx);
        }
    }

    // ========================================================================
    //  八、任务节点 (BTTask)
    // ========================================================================

    /// <summary>
    /// 任务节点基类。行为树叶子节点，执行具体动作。
    /// </summary>

    /// <summary>移动到目标位置。</summary>

    /// <summary>
    /// V1 任务节点基类（去重修复时恢复：与 V2 的 BTTask 隔离）。
    /// 密封 Execute，将执行委托给受保护的 ExecuteTask。
    /// </summary>
    public abstract class BTTaskV1 : BTNode
    {
        public sealed override BTNodeState Execute(BTNodeContext ctx)
        {
            LastState = ExecuteTask(ctx);
            return LastState;
        }

        /// <summary>任务具体逻辑。</summary>
        protected abstract BTNodeState ExecuteTask(BTNodeContext ctx);
    }

    public sealed class BTTask_MoveTo : BTTaskV1
    {
        public string TargetKey { get; set; } = "MoveTarget";
        public float AcceptanceRadius { get; set; } = 1f;
        public float Speed { get; set; } = 5f;

        public BTTask_MoveTo() { NodeName = "MoveTo"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            var target = ctx.Blackboard?.GetValue<Vector3>(TargetKey) ?? Vector3.Zero;
            if (ctx.UserData is IAIMovable movable)
            {
                var currentPos = movable.GetPosition();
                float dist = Vector3.Distance(currentPos, target);
                if (dist <= AcceptanceRadius)
                    return BTNodeState.Success;
                movable.MoveTo(target);
                return BTNodeState.Running;
            }
            return BTNodeState.Success;
        }
    }

    /// <summary>等待N秒。</summary>
    public sealed class BTTask_Wait : BTTaskV1
    {
        public float WaitTime { get; set; } = 1f;
        public float RandomDeviation { get; set; } = 0f;
        private float _elapsed = 0f;
        private float _targetWait = 1f;

        public BTTask_Wait() { NodeName = "Wait"; }

        public override void InitNode(BTNodeContext ctx)
        {
            base.InitNode(ctx);
            _targetWait = WaitTime;
            if (RandomDeviation > 0f)
            {
                var rng = new Random();
                _targetWait += (float)(rng.NextDouble() * 2 - 1) * RandomDeviation;
            }
            _elapsed = 0f;
        }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            _elapsed += ctx.DeltaTime;
            if (_elapsed >= _targetWait)
            {
                _elapsed = 0f;
                return BTNodeState.Success;
            }
            return BTNodeState.Running;
        }

        public override void Abort(BTNodeContext ctx) { _elapsed = 0f; base.Abort(ctx); }
    }

    /// <summary>播放动画。</summary>
    public sealed class BTTask_PlayAnim : BTTaskV1
    {
        public string AnimationName { get; set; } = "";
        public bool WaitUntilComplete { get; set; } = true;
        public float PlayRate { get; set; } = 1f;
        private bool _started = false;

        public BTTask_PlayAnim() { NodeName = "PlayAnim"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (!_started)
            {
                _started = true;
                if (ctx.UserData is IAIAnimated animated)
                    animated.PlayAnimation(AnimationName);
            }

            if (WaitUntilComplete)
            {
                if (ctx.UserData is IAIAnimated animated2 && animated2.IsAnimationPlaying(AnimationName))
                    return BTNodeState.Running;
                _started = false;
                return BTNodeState.Success;
            }

            _started = false;
            return BTNodeState.Success;
        }

        public override void Abort(BTNodeContext ctx) { _started = false; base.Abort(ctx); }
    }

    /// <summary>等待黑板变量时间。</summary>
    public sealed class BTTask_WaitBlackboardTime : BTTaskV1
    {
        public string TimeKey { get; set; } = "WaitTime";
        private float _elapsed = 0f;
        private float _targetWait = 1f;

        public BTTask_WaitBlackboardTime() { NodeName = "WaitBlackboardTime"; }

        public override void InitNode(BTNodeContext ctx)
        {
            base.InitNode(ctx);
            _targetWait = ctx.Blackboard?.GetValue<float>(TimeKey) ?? 1f;
            _elapsed = 0f;
        }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            _elapsed += ctx.DeltaTime;
            if (_elapsed >= _targetWait)
            {
                _elapsed = 0f;
                return BTNodeState.Success;
            }
            return BTNodeState.Running;
        }

        public override void Abort(BTNodeContext ctx) { _elapsed = 0f; base.Abort(ctx); }
    }

    /// <summary>设置黑板值。</summary>
    public sealed class BTTask_SetBlackboardValue : BTTaskV1
    {
        public string KeyName { get; set; } = "";
        public object Value { get; set; }
        public string ValueKey { get; set; } = ""; // 从另一个黑板键读取值

        public BTTask_SetBlackboardValue() { NodeName = "SetBlackboardValue"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (ctx.Blackboard == null || string.IsNullOrEmpty(KeyName))
                return BTNodeState.Failed;

            object val = Value;
            if (!string.IsNullOrEmpty(ValueKey))
                val = ctx.Blackboard.GetDynamicValue<object>(ValueKey) ?? Value;

            ctx.Blackboard.SetDynamicValue(KeyName, val);
            return BTNodeState.Success;
        }
    }

    /// <summary>清除黑板值。</summary>
    public sealed class BTTask_ClearBlackboardValue : BTTaskV1
    {
        public string KeyName { get; set; } = "";

        public BTTask_ClearBlackboardValue() { NodeName = "ClearBlackboardValue"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (ctx.Blackboard != null && !string.IsNullOrEmpty(KeyName))
            {
                ctx.Blackboard.ClearValue(KeyName);
                return BTNodeState.Success;
            }
            return BTNodeState.Failed;
        }
    }

    /// <summary>转向目标。</summary>
    public sealed class BTTask_TurnTo : BTTaskV1
    {
        public string TargetKey { get; set; } = "LookTarget";
        public float ToleranceAngle { get; set; } = 5f; // 度
        public float TurnSpeed { get; set; } = 180f;

        public BTTask_TurnTo() { NodeName = "TurnTo"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (ctx.UserData is IAITurnable turnable)
            {
                var target = ctx.Blackboard?.GetValue<Vector3>(TargetKey) ?? Vector3.Zero;
                bool reached = turnable.TurnTo(target, ToleranceAngle, ctx.DeltaTime, TurnSpeed);
                return reached ? BTNodeState.Success : BTNodeState.Running;
            }
            return BTNodeState.Success;
        }
    }

    /// <summary>运行子行为树。</summary>
    public sealed class BTTask_RunBehaviorTree : BTTaskV1
    {
        public BehaviorTree SubTreeAsset { get; set; }
        private BehaviorTreeInstance _subInstance;

        public BTTask_RunBehaviorTree() { NodeName = "RunBehaviorTree"; }

        public override void InitNode(BTNodeContext ctx)
        {
            base.InitNode(ctx);
            if (SubTreeAsset != null)
                _subInstance = SubTreeAsset.CreateInstance(ctx.TreeInstance?.Blackboard);
        }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (_subInstance == null) return BTNodeState.Failed;

            var result = _subInstance.Update(ctx.DeltaTime);
            if (result != BTNodeState.Running)
                _subInstance = null;
            return result;
        }

        public override void Abort(BTNodeContext ctx)
        {
            _subInstance?.Abort();
            _subInstance = null;
            base.Abort(ctx);
        }
    }

    /// <summary>Actor通用动作回调。</summary>
    public sealed class BTTask_ActorAction : BTTaskV1
    {
        public string ActionName { get; set; } = "";
        public Action<BTNodeContext> OnExecute { get; set; }
        public Func<BTNodeContext, BTNodeState> ActionFunc { get; set; }

        public BTTask_ActorAction() { NodeName = "ActorAction"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (ActionFunc != null)
                return ActionFunc(ctx);
            OnExecute?.Invoke(ctx);
            return BTNodeState.Success;
        }
    }

    /// <summary>生成Actor。</summary>
    public sealed class BTTask_SpawnActor : BTTaskV1
    {
        public string SpawnLocationKey { get; set; } = "SpawnLocation";
        public Func<BTNodeContext, ShanyuEngine.GameFramework.Actor> SpawnFunc { get; set; }

        public BTTask_SpawnActor() { NodeName = "SpawnActor"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (SpawnFunc != null)
            {
                var actor = SpawnFunc(ctx);
                ctx.Blackboard?.SetDynamicValue("SpawnedActor", actor);
                return BTNodeState.Success;
            }
            return BTNodeState.Failed;
        }
    }

    /// <summary>随机选择。</summary>
    public sealed class BTTask_RandomSelector : BTTaskV1
    {
        private readonly Random _rng = new Random();

        public BTTask_RandomSelector() { NodeName = "RandomSelector"; }

        /// <summary>候选列表。</summary>
        public List<Func<BTNodeContext, BTNodeState>> Options { get; } = new List<Func<BTNodeContext, BTNodeState>>();

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            if (Options.Count == 0) return BTNodeState.Failed;
            int idx = _rng.Next(Options.Count);
            return Options[idx](ctx);
        }
    }

    /// <summary>调试日志。</summary>
    public sealed class BTTask_Log : BTTaskV1
    {
        public string LogMessage { get; set; } = "BT Log";
        public string LogKey { get; set; } = ""; // 从黑板读取消息

        public BTTask_Log() { NodeName = "Log"; }

        protected override BTNodeState ExecuteTask(BTNodeContext ctx)
        {
            string msg = LogMessage;
            if (!string.IsNullOrEmpty(LogKey) && ctx.Blackboard != null)
                msg = ctx.Blackboard.GetValue<string>(LogKey) ?? LogMessage;
            Console.WriteLine($"[BT:{NodeName}] {msg}");
            return BTNodeState.Success;
        }
    }

    // ========================================================================
    //  九、AI 接口
    // ========================================================================

    public interface IAIMovable
    {
        Vector3 GetPosition();
        void MoveTo(Vector3 target);
    }

    public interface IAIAnimated
    {
        void PlayAnimation(string animName);
        bool IsAnimationPlaying(string animName);
    }

    public interface IAIAttackable
    {
        void PerformAttack(Vector3 targetPosition);
    }

    public interface IAITurnable
    {
        bool TurnTo(Vector3 target, float toleranceAngle, float deltaTime, float turnSpeed);
    }

    // ========================================================================
    //  十、行为树资产 (BehaviorTree)
    // ========================================================================

    /// <summary>
    /// 行为树资产。定义行为树的结构（Root 节点 + Blackboard 定义）。
    /// </summary>
    public class BehaviorTree
    {
        public Guid AssetId { get; set; } = Guid.NewGuid();
        public string AssetName { get; set; } = "UntitledBehaviorTree";
        public BTNode RootNode { get; set; }
        public BTBlackboardDefinition BlackboardDefinition { get; set; } = new BTBlackboardDefinition();
        public string Description { get; set; } = "";

        /// <summary>Update频率（秒），0表示每帧更新。</summary>
        public float UpdateInterval { get; set; } = 0f;

        public BehaviorTreeInstance CreateInstance()
        {
            var bb = BlackboardDefinition.CreateBlackboard();
            BTNode clonedRoot = RootNode?.Clone();
            return new BehaviorTreeInstance(this, bb, clonedRoot);
        }

        public BehaviorTreeInstance CreateInstance(Blackboard sharedBlackboard)
        {
            BTNode clonedRoot = RootNode?.Clone();
            return new BehaviorTreeInstance(this, sharedBlackboard, clonedRoot);
        }

        public Dictionary<string, object> Save()
        {
            return new Dictionary<string, object>
            {
                { "assetId", AssetId.ToString() },
                { "assetName", AssetName },
                { "description", Description },
                { "blackboard", BlackboardDefinition.SerializeKeys() },
                { "updateInterval", UpdateInterval }
            };
        }

        public void Load(Dictionary<string, object> data)
        {
            if (data.TryGetValue("assetId", out var id) && id is string idStr)
                AssetId = Guid.Parse(idStr);
            if (data.TryGetValue("assetName", out var name) && name is string nameStr)
                AssetName = nameStr;
            if (data.TryGetValue("description", out var desc) && desc is string descStr)
                Description = descStr;
            if (data.TryGetValue("updateInterval", out var interval) && interval is float f)
                UpdateInterval = f;
        }
    }

    /// <summary>兼容别名。</summary>
    public sealed class BTTree : BehaviorTree
    {
        public BTTree() : base() { }
    }

    // ========================================================================
    //  十一、行为树运行时实例 (BehaviorTreeInstance)
    // ========================================================================

    /// <summary>
    /// 行为树运行时实例。每个 AI 实体拥有一个实例。
    /// </summary>
    public class BehaviorTreeInstance
    {
        public BehaviorTree Tree { get; }
        public Blackboard Blackboard { get; }
        public BTNode RootNode { get; private set; }
        public BTNodeContext Context { get; }
        public BTNodeState CurrentState { get; private set; } = BTNodeState.Inactive;
        public bool IsRunning { get; private set; }
        public bool IsSuspended { get; set; }

        public Action<BTNode, BTNodeContext> OnBreakpointHit { get; set; }
        public Stack<BTNode> ExecutionStack { get; } = new Stack<BTNode>();

        /// <summary>上次 Update 的时间。</summary>
        public float LastUpdateTime { get; private set; }

        internal BehaviorTreeInstance(BehaviorTree tree, Blackboard blackboard, BTNode rootNode = null)
        {
            Tree = tree;
            Blackboard = blackboard ?? tree?.CreateInstance().Blackboard;
            RootNode = rootNode;
            Context = new BTNodeContext(this);
        }

        public BTNodeState Update(float deltaTime)
        {
            if (IsSuspended || RootNode == null)
                return BTNodeState.Failed;

            // Update频率限制
            if (Tree != null && Tree.UpdateInterval > 0f)
            {
                LastUpdateTime += deltaTime;
                if (LastUpdateTime < Tree.UpdateInterval)
                    return CurrentState;
                LastUpdateTime -= Tree.UpdateInterval;
            }

            IsRunning = true;
            Context.DeltaTime = deltaTime;
            ExecutionStack.Clear();

            var result = RootNode.ExecuteWithDecorators(Context);
            CurrentState = result;
            RootNode.LastState = result;

            return result;
        }

        public void Abort()
        {
            RootNode?.Abort(Context);
            CurrentState = BTNodeState.Aborted;
            IsRunning = false;
        }

        public void Suspend() { IsSuspended = true; }
        public void Resume() { IsSuspended = false; }

        public void Cleanup()
        {
            RootNode?.Cleanup(Context);
            CurrentState = BTNodeState.Inactive;
            IsRunning = false;
            IsSuspended = false;
            ExecutionStack.Clear();
        }

        public void Restart()
        {
            Cleanup();
            RootNode = Tree?.RootNode?.Clone();
            IsRunning = true;
        }
    }

    /// <summary>兼容别名。</summary>
    public sealed class BTInstance : BehaviorTreeInstance
    {
        internal BTInstance(BehaviorTree tree, Blackboard blackboard, BTNode rootNode = null)
            : base(tree, blackboard, rootNode) { }
    }

    // ========================================================================
    //  十二、行为树管理器 (BehaviorTreeManager)
    // ========================================================================

    /// <summary>
    /// 行为树全局管理器。管理树资产注册、实例池、全局Tick。
    /// </summary>
    public sealed class BehaviorTreeManager
    {
        private static readonly Lazy<BehaviorTreeManager> _instance =
            new Lazy<BehaviorTreeManager>(() => new BehaviorTreeManager());
        public static BehaviorTreeManager Instance => _instance.Value;

        private readonly Dictionary<Guid, BehaviorTree> _registeredTrees = new Dictionary<Guid, BehaviorTree>();
        private readonly Dictionary<Guid, Stack<BehaviorTreeInstance>> _instancePool =
            new Dictionary<Guid, Stack<BehaviorTreeInstance>>();
        private readonly List<BehaviorTreeInstance> _activeInstances = new List<BehaviorTreeInstance>();
        private int _maxPoolSize = 64;

        public int MaxPoolSize
        {
            get => _maxPoolSize;
            set => _maxPoolSize = Math.Max(1, value);
        }

        public int ActiveInstanceCount => _activeInstances.Count;

        /// <summary>注册行为树资产。</summary>
        public void RegisterTree(BehaviorTree tree)
        {
            if (tree == null) return;
            _registeredTrees[tree.AssetId] = tree;
            if (!_instancePool.ContainsKey(tree.AssetId))
                _instancePool[tree.AssetId] = new Stack<BehaviorTreeInstance>();
        }

        /// <summary>注销行为树资产。</summary>
        public void UnregisterTree(Guid assetId)
        {
            _registeredTrees.Remove(assetId);
            if (_instancePool.TryGetValue(assetId, out var pool))
            {
                foreach (var inst in pool) inst.Cleanup();
                pool.Clear();
                _instancePool.Remove(assetId);
            }
        }

        /// <summary>获取已注册的行为树。</summary>
        public BehaviorTree GetTree(Guid assetId)
        {
            _registeredTrees.TryGetValue(assetId, out var tree);
            return tree;
        }

        /// <summary>创建实例。</summary>
        public BehaviorTreeInstance CreateInstance(Guid treeAssetId)
        {
            if (!_registeredTrees.TryGetValue(treeAssetId, out var tree)) return null;
            return tree.CreateInstance();
        }

        /// <summary>从池中获取实例。</summary>
        public BehaviorTreeInstance AcquireInstance(Guid treeAssetId)
        {
            if (_instancePool.TryGetValue(treeAssetId, out var pool) && pool.Count > 0)
                return pool.Pop();
            return CreateInstance(treeAssetId);
        }

        /// <summary>归还实例到池中。</summary>
        public void ReleaseInstance(BehaviorTreeInstance instance)
        {
            if (instance == null) return;
            instance.Cleanup();
            var assetId = instance.Tree?.AssetId ?? Guid.Empty;
            if (!_instancePool.TryGetValue(assetId, out var pool))
            {
                pool = new Stack<BehaviorTreeInstance>();
                _instancePool[assetId] = pool;
            }
            if (pool.Count < _maxPoolSize)
                pool.Push(instance);
            _activeInstances.Remove(instance);
        }

        /// <summary>激活实例（加入全局Tick列表）。</summary>
        public void ActivateInstance(BehaviorTreeInstance instance)
        {
            if (instance == null) return;
            if (!_activeInstances.Contains(instance))
                _activeInstances.Add(instance);
        }

        /// <summary>停用实例（从全局Tick列表移除）。</summary>
        public void DeactivateInstance(BehaviorTreeInstance instance)
        {
            _activeInstances.Remove(instance);
        }

        /// <summary>全局 Tick：更新所有活跃实例。</summary>
        public void TickAll(float deltaTime)
        {
            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                var inst = _activeInstances[i];
                inst.Update(deltaTime);
            }
        }

        /// <summary>暂停所有实例。</summary>
        public void SuspendAll()
        {
            foreach (var inst in _activeInstances)
                inst.Suspend();
        }

        /// <summary>恢复所有实例。</summary>
        public void ResumeAll()
        {
            foreach (var inst in _activeInstances)
                inst.Resume();
        }

        /// <summary>中止所有实例。</summary>
        public void AbortAll()
        {
            foreach (var inst in _activeInstances)
                inst.Abort();
        }

        /// <summary>清空实例池。</summary>
        public void ClearPool()
        {
            foreach (var pool in _instancePool.Values) pool.Clear();
        }

        /// <summary>获取所有已注册树的名称。</summary>
        public List<string> GetRegisteredTreeNames()
        {
            return _registeredTrees.Values.Select(t => t.AssetName).ToList();
        }
    }

    // ========================================================================
    //  十三、实例池 (BTInstancePool)
    // ========================================================================

    /// <summary>
    /// 行为树实例池（兼容旧名称）。
    /// </summary>
    public sealed class BTInstancePool
    {
        private readonly Dictionary<Guid, Stack<BehaviorTreeInstance>> _pool =
            new Dictionary<Guid, Stack<BehaviorTreeInstance>>();
        private readonly Dictionary<Guid, BehaviorTree> _assets =
            new Dictionary<Guid, BehaviorTree>();
        private int _maxPoolSize = 64;

        public int MaxPoolSize { get => _maxPoolSize; set => _maxPoolSize = Math.Max(1, value); }

        public void RegisterAsset(BehaviorTree asset)
        {
            if (asset == null) return;
            _assets[asset.AssetId] = asset;
            if (!_pool.ContainsKey(asset.AssetId))
                _pool[asset.AssetId] = new Stack<BehaviorTreeInstance>();
        }

        public BehaviorTreeInstance Acquire(Guid assetId)
        {
            if (_pool.TryGetValue(assetId, out var stack) && stack.Count > 0)
                return stack.Pop();
            if (_assets.TryGetValue(assetId, out var asset))
                return asset.CreateInstance();
            return null;
        }

        public void Release(BehaviorTreeInstance instance)
        {
            if (instance == null) return;
            var assetId = instance.Tree?.AssetId ?? Guid.Empty;
            if (!_pool.TryGetValue(assetId, out var stack))
            {
                stack = new Stack<BehaviorTreeInstance>();
                _pool[assetId] = stack;
            }
            if (stack.Count < _maxPoolSize)
            {
                instance.Cleanup();
                stack.Push(instance);
            }
        }

        public void Clear()
        {
            foreach (var stack in _pool.Values) stack.Clear();
        }

        public int TotalPooled => _pool.Values.Sum(s => s.Count);
    }

    // ========================================================================
    //  十四、行为树编辑器集成
    // ========================================================================

    public sealed class BTVisualNode
    {
        public BTNode Node { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Size { get; set; } = new Vector2(200, 60);
        public Vector3 NodeColor { get; set; } = new Vector3(0.2f, 0.6f, 1f);
    }

    public sealed class BTVisualConnection
    {
        public BTVisualNode From { get; set; }
        public BTVisualNode To { get; set; }
    }

    public sealed class BTVisualGraph
    {
        public List<BTVisualNode> Nodes { get; } = new List<BTVisualNode>();
        public List<BTVisualConnection> Connections { get; } = new List<BTVisualConnection>();

        public void BuildFromTree(BehaviorTree tree)
        {
            Nodes.Clear();
            Connections.Clear();
            if (tree?.RootNode == null) return;
            BuildNodeRecursive(tree.RootNode, null, 0, 0);
        }

        private BTVisualNode BuildNodeRecursive(BTNode node, BTVisualNode parent, int depth, int siblingIndex)
        {
            var visual = new BTVisualNode
            {
                Node = node,
                Position = new Vector2(siblingIndex * 250, depth * 120)
            };

            switch (node)
            {
                case BTComposite _: visual.NodeColor = new Vector3(0.2f, 0.8f, 0.2f); break;
                case BTDecorator _: visual.NodeColor = new Vector3(0.8f, 0.8f, 0.2f); break;
                case BTTask _: visual.NodeColor = new Vector3(0.2f, 0.5f, 0.9f); break;
            }

            Nodes.Add(visual);
            if (parent != null)
                Connections.Add(new BTVisualConnection { From = parent, To = visual });

            for (int i = 0; i < node.Children.Count; i++)
                BuildNodeRecursive(node.Children[i], visual, depth + 1, i);

            return visual;
        }
    }

    public sealed class BTDebugger
    {
        public BehaviorTreeInstance ActiveInstance { get; set; }
        public bool IsEnabled { get; set; }

        public BTNode GetCurrentNode() => ActiveInstance?.Context?.ActiveNode;

        public string GetStateDescription()
        {
            if (ActiveInstance == null) return "No active instance";
            return $"State: {ActiveInstance.CurrentState} | Running: {ActiveInstance.IsRunning} | Suspended: {ActiveInstance.IsSuspended}";
        }

        public List<BTNode> GetExecutionPath()
        {
            var path = new List<BTNode>();
            if (ActiveInstance?.Context?.ActiveNode == null) return path;
            var node = ActiveInstance.Context.ActiveNode;
            while (node != null)
            {
                path.Add(node);
                node = node.Parent;
            }
            path.Reverse();
            return path;
        }

        public void SetBreakpoint(BTNode node, bool enable)
        {
            if (node != null) node.HasBreakpoint = enable;
        }

        public void ClearAllBreakpoints()
        {
            if (ActiveInstance?.RootNode == null) return;
            ClearBreakpointsRecursive(ActiveInstance.RootNode);
        }

        private void ClearBreakpointsRecursive(BTNode node)
        {
            node.HasBreakpoint = false;
            foreach (var child in node.Children)
                ClearBreakpointsRecursive(child);
        }
    }
}
