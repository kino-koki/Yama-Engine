// ============================================================================
//  YamaEngine.AI.BehaviorTreeV2.cs
//  yama引擎 -- 行为树 V2 (带黑板 + 装饰器)
//  对齐 UE5 BehaviorTree / Behavior Designer / BrainSim
// ----------------------------------------------------------------------------
//  本文件实现一套完整的工业级行为树：
//    1. Blackboard        - 共享内存(变量存储/类型安全)
//    2. BTNodeV2 基类       - 抽象节点 + 执行结果枚举
//    3. Composite 节点    - Sequence / Selector / Parallel / Random
//    4. Decorator 节点    - 条件/反转/重复/超时/冷却/黑板检查
//    5. Task 节点         - 等待/移动/播放动画/自定义 lambda
//    6. BTContext         - 执行上下文(黑板引用/agent 引用/事件队列)
//    7. BehaviorTreeV2    - 主类(根节点/黑板/Tick)
//    8. BTBuilder         - 流式 API 构建
// ----------------------------------------------------------------------------
//  注意：行为树不能有循环引用，否则 tick 会无限递归。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
namespace YamaEngine.AI
{
    // ========================================================================
    //  一、执行状态
    // ========================================================================

    /// <summary>节点执行结果。</summary>
    public enum BTNodeStatus
    {
        /// <summary>成功完成。</summary>
        Success,
        /// <summary>失败。</summary>
        Failure,
        /// <summary>失败（别名，等价 <see cref="Failure"/>）。</summary>
        Failed = Failure,
        /// <summary>正在执行，下帧继续。</summary>
        Running,
        /// <summary>尚未开始或被中止。</summary>
        NotStarted,
        /// <summary>被高优先级节点打断。</summary>
        Aborted,
        /// <summary>执行出错。</summary>
        Error
    }

    // ========================================================================
    //  二、黑板
    // ========================================================================

    /// <summary>
    /// 黑板键类型。运行时可动态加 key，但类型要预先声明。
    /// </summary>

    /// <summary>黑板条目。</summary>
    public sealed class BlackboardEntry
    {
        public string Key;
        public BlackboardKeyType Type;
        public object? Value;
        public bool IsSynced; // 是否需要同步到其他 agent
        public event Action? OnChanged;

        public BlackboardEntry(string key, BlackboardKeyType type)
        {
            Key = key; Type = type; Value = null;
        }

        public void NotifyChanged() => OnChanged?.Invoke();

        public T? GetValue<T>()
        {
            if (Value is T t) return t;
            return default;
        }

        public void SetValue(object? v)
        {
            Value = v;
            NotifyChanged();
        }
    }

    /// <summary>
    /// 黑板。行为树共享内存，节点之间传数据用。
    /// </summary>

    // ========================================================================
    //  三、BT 节点基类
    // ========================================================================

    /// <summary>
    /// 行为树节点基类。所有节点都从这里派生。
    /// 节点是无状态的(除了 running 状态)，可以被多个 agent 共享。
    /// </summary>

    // ========================================================================
    //  四、Composite 节点(组合节点)
    // ========================================================================

    /// <summary>
    /// Sequence: 按顺序执行子节点，全部成功才成功，任一失败就失败。
    /// 类似 AND。
    /// </summary>

    // ========================================================================
    //  二点五、V2 节点基类（去重修复时恢复：V2 节点需要独立的执行上下文签名）
    // ========================================================================

    /// <summary>V2 行为树节点基类。与 V1 的 BTNodeV2 隔离，使用 BTContext/BTNodeStatus。</summary>
    public abstract class BTNodeV2
    {
        /// <summary>节点显示名。</summary>
        public string Name = "Node";
        /// <summary>父节点（根节点为 null）。</summary>
        public BTNodeV2? Parent;
        /// <summary>子节点列表（复合节点使用）。</summary>
        public readonly List<BTNodeV2> Children = new();
        /// <summary>最近一次执行状态。</summary>
        public BTNodeStatus Status { get; protected set; } = BTNodeStatus.NotStarted;
        /// <summary>是否正在运行。</summary>
        public bool IsRunning => Status == BTNodeStatus.Running;

        /// <summary>执行节点逻辑。</summary>
        public abstract BTNodeStatus Execute(BTContext context);

        /// <summary>重置本节点及所有子节点状态。</summary>
        public virtual void Reset()
        {
            Status = BTNodeStatus.NotStarted;
            foreach (var child in Children) child.Reset();
        }

        /// <summary>被中止时的回调。</summary>
        public virtual void OnAbort(BTContext context) { }

        /// <summary>中止节点。</summary>
        public virtual void Abort(BTContext context)
        {
            OnAbort(context);
            Status = BTNodeStatus.Aborted;
        }

        /// <summary>标记为运行中。</summary>
        protected void StartRunning(BTContext context) => Status = BTNodeStatus.Running;

        /// <summary>停止运行（供子类在结束/失败时调用）。</summary>
        protected void StopRunning(BTContext context) => Status = BTNodeStatus.NotStarted;

        /// <summary>添加子节点并设置其父引用。</summary>
        public T AddChild<T>(T child) where T : BTNodeV2
        {
            child.Parent = this;
            Children.Add(child);
            return child;
        }
    }

    /// <summary>V2 装饰器基类：持有一个子节点。</summary>
    public abstract class BTDecoratorV2 : BTNodeV2
    {
        /// <summary>被装饰的子节点。</summary>
        public BTNodeV2? Child;

        protected BTDecoratorV2(BTNodeV2 child)
        {
            Child = child;
            child.Parent = this;
        }

        public override void Reset()
        {
            base.Reset();
            Child?.Reset();
        }

        /// <summary>设置被装饰的子节点（替换默认的 NoOp）。</summary>
        public void SetChild(BTNodeV2 child)
        {
            Child = child;
            child.Parent = this;
        }
    }

    public sealed class BTSequence : BTNodeV2
    {
        private int _currentChild = 0;

        public BTSequence() { Name = "Sequence"; }
        public BTSequence(params BTNodeV2[] children) { Name = "Sequence"; Children.AddRange(children); }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (_currentChild >= Children.Count)
            {
                Status = BTNodeStatus.Success;
                _currentChild = 0;
                return Status;
            }
            var child = Children[_currentChild];
            var status = child.Execute(context);
            switch (status)
            {
                case BTNodeStatus.Success:
                    _currentChild++;
                    if (_currentChild >= Children.Count)
                    {
                        Status = BTNodeStatus.Success;
                        _currentChild = 0;
                        return Status;
                    }
                    Status = BTNodeStatus.Running;
                    return Status;
                case BTNodeStatus.Failure:
                    Status = BTNodeStatus.Failure;
                    _currentChild = 0;
                    return Status;
                case BTNodeStatus.Running:
                    Status = BTNodeStatus.Running;
                    return Status;
                default:
                    Status = status;
                    _currentChild = 0;
                    return Status;
            }
        }

        public override void Reset()
        {
            _currentChild = 0;
            base.Reset();
        }
    }

    /// <summary>
    /// Selector: 按顺序尝试子节点，任一成功就成功，全部失败才失败。
    /// 类似 OR。
    /// </summary>
    public sealed class BTSelector : BTNodeV2
    {
        private int _currentChild = 0;

        public BTSelector() { Name = "Selector"; }
        public BTSelector(params BTNodeV2[] children) { Name = "Selector"; Children.AddRange(children); }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (_currentChild >= Children.Count)
            {
                Status = BTNodeStatus.Failure;
                _currentChild = 0;
                return Status;
            }
            var child = Children[_currentChild];
            var status = child.Execute(context);
            switch (status)
            {
                case BTNodeStatus.Success:
                    Status = BTNodeStatus.Success;
                    _currentChild = 0;
                    return Status;
                case BTNodeStatus.Failure:
                    _currentChild++;
                    if (_currentChild >= Children.Count)
                    {
                        Status = BTNodeStatus.Failure;
                        _currentChild = 0;
                        return Status;
                    }
                    Status = BTNodeStatus.Running;
                    return Status;
                case BTNodeStatus.Running:
                    Status = BTNodeStatus.Running;
                    return Status;
                default:
                    Status = status;
                    _currentChild = 0;
                    return Status;
            }
        }

        public override void Reset()
        {
            _currentChild = 0;
            base.Reset();
        }
    }

    /// <summary>
    /// Parallel: 同时执行所有子节点。
    /// - SuccessPolicy.All: 全部成功才成功
    /// - SuccessPolicy.Any: 任一成功就成功
    /// 失败策略同理。
    /// </summary>
    public enum BTParallelPolicy
    {
        All,
        Any
    }

    /// <summary>
    /// Parallel: 同时执行所有子节点。按 SuccessPolicy / FailurePolicy 判定整体结果。
    /// </summary>
    public sealed class BTParallel : BTNodeV2
    {
        public BTParallelPolicy SuccessPolicy = BTParallelPolicy.All;
        public BTParallelPolicy FailurePolicy = BTParallelPolicy.Any;

        public BTParallel() { Name = "Parallel"; }
        public BTParallel(params BTNodeV2[] children) { Name = "Parallel"; Children.AddRange(children); }

        public override BTNodeStatus Execute(BTContext context)
        {
            int success = 0, fail = 0;
            foreach (var child in Children)
            {
                var status = child.Execute(context);
                if (status == BTNodeStatus.Success) { success++; continue; }
                if (status == BTNodeStatus.Failure) { fail++; continue; }
                // 任一仍在执行则整体 Running
                Status = BTNodeStatus.Running;
                return Status;
            }
            bool ok = SuccessPolicy == BTParallelPolicy.All ? success == Children.Count : success > 0;
            bool bad = FailurePolicy == BTParallelPolicy.All ? fail == Children.Count : fail > 0;
            if (bad) { Status = BTNodeStatus.Failure; return Status; }
            if (ok) { Status = BTNodeStatus.Success; return Status; }
            Status = BTNodeStatus.Running;
            return Status;
        }
    }

    /// <summary>
    /// RandomSelector: 随机顺序执行子节点(打乱后当 Selector)。
    /// 给 AI 加随机性用。
    /// </summary>
    public sealed class BTRandomSelector : BTNodeV2
    {
        private int _currentChild = 0;
        private int[]? _order;
        private Random _rng = new();

        public BTRandomSelector() { Name = "RandomSelector"; }
        public BTRandomSelector(params BTNodeV2[] children) { Name = "RandomSelector"; Children.AddRange(children); }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (_order == null || _order.Length != Children.Count)
            {
                _order = new int[Children.Count];
                for (int i = 0; i < _order.Length; i++) _order[i] = i;
                // Fisher-Yates shuffle
                for (int i = _order.Length - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (_order[i], _order[j]) = (_order[j], _order[i]);
                }
                _currentChild = 0;
            }
            while (_currentChild < _order.Length)
            {
                var child = Children[_order[_currentChild]];
                var status = child.Execute(context);
                switch (status)
                {
                    case BTNodeStatus.Success:
                        Status = BTNodeStatus.Success;
                        _order = null;
                        return Status;
                    case BTNodeStatus.Running:
                        Status = BTNodeStatus.Running;
                        return Status;
                    case BTNodeStatus.Failure:
                        _currentChild++;
                        break;
                    default:
                        Status = status;
                        _order = null;
                        return Status;
                }
            }
            Status = BTNodeStatus.Failure;
            _order = null;
            return Status;
        }

        public override void Reset() { _currentChild = 0; _order = null; base.Reset(); }
    }

    /// <summary>
    /// WeightedRandomSelector: 按权重随机选子节点执行。
    /// </summary>
    public sealed class BTWeightedRandomSelector : BTNodeV2
    {
        public List<float> Weights = new();
        private int _currentChild = -1;
        private Random _rng = new();

        public BTWeightedRandomSelector() { Name = "WeightedRandom"; }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (_currentChild < 0)
            {
                _currentChild = PickWeighted();
            }
            if (_currentChild < 0 || _currentChild >= Children.Count)
            {
                Status = BTNodeStatus.Failure;
                _currentChild = -1;
                return Status;
            }
            var status = Children[_currentChild].Execute(context);
            if (status != BTNodeStatus.Running)
                _currentChild = -1;
            Status = status;
            return status;
        }

        private int PickWeighted()
        {
            float total = 0;
            for (int i = 0; i < Weights.Count && i < Children.Count; i++) total += Weights[i];
            if (total <= 0) return Children.Count > 0 ? 0 : -1;
            float r = (float)_rng.NextDouble() * total;
            float acc = 0;
            for (int i = 0; i < Weights.Count && i < Children.Count; i++)
            {
                acc += Weights[i];
                if (r <= acc) return i;
            }
            return Children.Count - 1;
        }

        public override void Reset() { _currentChild = -1; base.Reset(); }
    }

    // ========================================================================
    //  五、Decorator 节点(装饰器)
    // ========================================================================

    /// <summary>装饰器基类：包装一个子节点，修改其行为。</summary>

    /// <summary>条件装饰器：条件满足才执行子节点。</summary>
    public sealed class BTCondition : BTDecoratorV2
    {
        public Func<BTContext, bool> Condition;
        public bool AbortOnFail = true; // 失败时是否中止子节点

        public BTCondition(Func<BTContext, bool> condition, BTNodeV2? child = null)
            : base(child ?? new BTNoOp())
        {
            Condition = condition;
            Name = "Condition";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (!Condition(context))
            {
                if (AbortOnFail && Child!.IsRunning)
                    Child.OnAbort(context);
                Status = BTNodeStatus.Failure;
                return Status;
            }
            Status = Child!.Execute(context);
            return Status;
        }
    }

    /// <summary>反转装饰器：把子节点结果 Success/Failure 互换。</summary>
    public sealed class BTInverter : BTDecoratorV2
    {
        public BTInverter(BTNodeV2? child = null) : base(child ?? new BTNoOp()) { Name = "Inverter"; }

        public override BTNodeStatus Execute(BTContext context)
        {
            var status = Child!.Execute(context);
            Status = status switch
            {
                BTNodeStatus.Success => BTNodeStatus.Failure,
                BTNodeStatus.Failure => BTNodeStatus.Success,
                _ => status
            };
            return Status;
        }
    }

    /// <summary>重复装饰器：子节点成功后重复执行 N 次。</summary>
    public sealed class BTRepeater : BTDecoratorV2
    {
        public int Count = -1; // -1=无限
        public bool RepeatOnFailure = false;
        private int _current = 0;

        public BTRepeater(BTNodeV2? child = null, int count = -1) : base(child ?? new BTNoOp())
        {
            Count = count; Name = "Repeater";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            while (true)
            {
                var status = Child!.Execute(context);
                if (status == BTNodeStatus.Running)
                {
                    Status = BTNodeStatus.Running;
                    return Status;
                }
                if (status == BTNodeStatus.Failure && !RepeatOnFailure)
                {
                    Status = BTNodeStatus.Failure;
                    _current = 0;
                    return Status;
                }
                _current++;
                if (Count > 0 && _current >= Count)
                {
                    Status = BTNodeStatus.Success;
                    _current = 0;
                    return Status;
                }
                // 重置子节点继续重复
                Child.Reset();
            }
        }

        public override void Reset() { _current = 0; base.Reset(); }
    }

    /// <summary>超时装饰器：超时则中止子节点并失败。</summary>
    public sealed class BTTimeout : BTDecoratorV2
    {
        public float TimeoutSeconds;
        private float _elapsed;

        public BTTimeout(float seconds, BTNodeV2? child = null) : base(child ?? new BTNoOp())
        {
            TimeoutSeconds = seconds; Name = "Timeout";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            _elapsed += context.DeltaTime;
            if (_elapsed >= TimeoutSeconds)
            {
                Child!.OnAbort(context);
                Status = BTNodeStatus.Failure;
                _elapsed = 0;
                return Status;
            }
            var status = Child!.Execute(context);
            if (status != BTNodeStatus.Running)
                _elapsed = 0;
            Status = status;
            return status;
        }

        public override void Reset() { _elapsed = 0; base.Reset(); }
    }

    /// <summary>冷却装饰器：子节点完成后强制等待一段时间才能再次执行。</summary>
    public sealed class BTCoolDown : BTDecoratorV2
    {
        public float CoolDownSeconds;
        private float _timer;

        public BTCoolDown(float seconds, BTNodeV2? child = null) : base(child ?? new BTNoOp())
        {
            CoolDownSeconds = seconds; Name = "CoolDown";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (_timer > 0)
            {
                _timer -= context.DeltaTime;
                Status = BTNodeStatus.Failure;
                return Status;
            }
            var status = Child!.Execute(context);
            if (status == BTNodeStatus.Success || status == BTNodeStatus.Failure)
                _timer = CoolDownSeconds;
            Status = status;
            return status;
        }

        public override void Reset() { _timer = 0; base.Reset(); }
    }

    /// <summary>黑板检查装饰器。</summary>
    public sealed class BTBlackboardCheck : BTDecoratorV2
    {
        public string Key;
        public BlackboardKeyType KeyType;
        public object ExpectedValue;
        public ComparisonOp Op;

        public enum ComparisonOp
        {
            Equal,
            NotEqual,
            Greater,
            Less,
            GreaterEqual,
            LessEqual,
            IsSet,
            IsNotSet
        }

        public BTBlackboardCheck(string key, BlackboardKeyType type, ComparisonOp op, object? expected = null, BTNodeV2? child = null)
            : base(child ?? new BTNoOp())
        {
            Key = key; KeyType = type; Op = op; ExpectedValue = expected ?? default;
            Name = "BBCheck";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            var bb = context.Blackboard;
            bool result = Op switch
            {
                ComparisonOp.IsSet => bb.HasKey(Key),
                ComparisonOp.IsNotSet => !bb.HasKey(Key),
                ComparisonOp.Equal when KeyType == BlackboardKeyType.Bool => bb.GetBool(Key) == (bool)ExpectedValue,
                ComparisonOp.Equal when KeyType == BlackboardKeyType.Int => bb.GetInt(Key) == (int)ExpectedValue,
                ComparisonOp.Equal when KeyType == BlackboardKeyType.Float => MathF.Abs(bb.GetFloat(Key) - (float)ExpectedValue) < 1e-4f,
                ComparisonOp.NotEqual when KeyType == BlackboardKeyType.Bool => bb.GetBool(Key) != (bool)ExpectedValue,
                ComparisonOp.NotEqual when KeyType == BlackboardKeyType.Int => bb.GetInt(Key) != (int)ExpectedValue,
                ComparisonOp.Greater when KeyType == BlackboardKeyType.Float => bb.GetFloat(Key) > (float)ExpectedValue,
                ComparisonOp.Less when KeyType == BlackboardKeyType.Float => bb.GetFloat(Key) < (float)ExpectedValue,
                ComparisonOp.GreaterEqual when KeyType == BlackboardKeyType.Float => bb.GetFloat(Key) >= (float)ExpectedValue,
                ComparisonOp.LessEqual when KeyType == BlackboardKeyType.Float => bb.GetFloat(Key) <= (float)ExpectedValue,
                ComparisonOp.Greater when KeyType == BlackboardKeyType.Int => bb.GetInt(Key) > (int)ExpectedValue,
                ComparisonOp.Less when KeyType == BlackboardKeyType.Int => bb.GetInt(Key) < (int)ExpectedValue,
                _ => false
            };
            if (!result)
            {
                if (Child!.IsRunning) Child.OnAbort(context);
                Status = BTNodeStatus.Failure;
                return Status;
            }
            Status = Child!.Execute(context);
            return Status;
        }
    }

    /// <summary>循环直到成功(找路失败重试场景)。</summary>
    public sealed class BTUntilSuccess : BTDecoratorV2
    {
        public int MaxAttempts = -1;
        private int _attempts;

        public BTUntilSuccess(BTNodeV2? child = null, int maxAttempts = -1) : base(child ?? new BTNoOp())
        {
            MaxAttempts = maxAttempts; Name = "UntilSuccess";
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            var status = Child!.Execute(context);
            if (status == BTNodeStatus.Success)
            {
                Status = BTNodeStatus.Success;
                _attempts = 0;
                return Status;
            }
            if (status == BTNodeStatus.Failure)
            {
                _attempts++;
                if (MaxAttempts > 0 && _attempts >= MaxAttempts)
                {
                    Status = BTNodeStatus.Failure;
                    _attempts = 0;
                    return Status;
                }
                Child.Reset();
                Status = BTNodeStatus.Running;
                return Status;
            }
            Status = status;
            return status;
        }

        public override void Reset() { _attempts = 0; base.Reset(); }
    }

    // ========================================================================
    //  六、Task 节点(叶子任务)
    // ========================================================================

    /// <summary>空操作节点(占位用)。</summary>
    public sealed class BTNoOp : BTNodeV2
    {
        public BTNoOp() { Name = "NoOp"; }
        public override BTNodeStatus Execute(BTContext context)
        {
            Status = BTNodeStatus.Success;
            return Status;
        }
    }

    /// <summary>等待指定时间。</summary>
    public sealed class BTWait : BTNodeV2
    {
        public float WaitTime;
        private float _elapsed;

        public BTWait(float seconds) { WaitTime = seconds; Name = $"Wait({seconds}s)"; }

        public override BTNodeStatus Execute(BTContext context)
        {
            _elapsed += context.DeltaTime;
            if (_elapsed >= WaitTime)
            {
                Status = BTNodeStatus.Success;
                _elapsed = 0;
                return Status;
            }
            Status = BTNodeStatus.Running;
            return Status;
        }

        public override void Reset() { _elapsed = 0; base.Reset(); }
    }

    /// <summary>等待黑板上某条件成立。</summary>
    public sealed class BTWaitForCondition : BTNodeV2
    {
        public Func<BTContext, bool> Condition;
        public float Timeout = -1;
        private float _elapsed;

        public BTWaitForCondition(Func<BTContext, bool> condition) { Condition = condition; Name = "WaitForCond"; }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (Condition(context))
            {
                Status = BTNodeStatus.Success;
                _elapsed = 0;
                return Status;
            }
            if (Timeout > 0)
            {
                _elapsed += context.DeltaTime;
                if (_elapsed >= Timeout)
                {
                    Status = BTNodeStatus.Failure;
                    _elapsed = 0;
                    return Status;
                }
            }
            Status = BTNodeStatus.Running;
            return Status;
        }

        public override void Reset() { _elapsed = 0; base.Reset(); }
    }

    /// <summary>
    /// 通用 Lambda 任务节点。用闭包快速实现任务逻辑。
    /// </summary>
    public class BTTask : BTNodeV2
    {
        public Func<BTContext, BTNodeStatus> Task;

        public BTTask(string name, Func<BTContext, BTNodeStatus> task)
        {
            Name = name; Task = task;
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            try
            {
                Status = Task(context);
            }
            catch (Exception ex)
            {
                context.Error($"Task '{Name}' threw: {ex.Message}");
                Status = BTNodeStatus.Error;
            }
            return Status;
        }
    }

    /// <summary>移动到目标位置(需要外部提供移动函数)。</summary>
    public sealed class BTMoveTo : BTNodeV2
    {
        public Func<BTContext, Vector3> GetTarget;
        public float AcceptanceRadius = 0.5f;
        public float UpdateInterval = 0.2f;
        private float _timer;
        private Vector3 _lastTarget;

        public BTMoveTo(Func<BTContext, Vector3> getTarget, float acceptance = 0.5f)
        {
            Name = "MoveTo"; GetTarget = getTarget; AcceptanceRadius = acceptance;
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            var target = GetTarget(context);
            // 周期性更新目标
            _timer -= context.DeltaTime;
            if (_timer <= 0 || Vector3.DistanceSquared(target, _lastTarget) > 1f)
            {
                context.Blackboard.SetVector3("BT_MoveTarget", target);
                context.OnMoveRequested?.Invoke(target);
                _lastTarget = target;
                _timer = UpdateInterval;
            }
            var agentPos = context.GetAgentPosition();
            float dist = Vector3.Distance(agentPos, target);
            if (dist <= AcceptanceRadius)
            {
                Status = BTNodeStatus.Success;
                return Status;
            }
            Status = BTNodeStatus.Running;
            return Status;
        }

        public override void OnAbort(BTContext context)
        {
            context.OnMoveAborted?.Invoke();
        }
    }

    /// <summary>播放动画任务。</summary>
    public sealed class BTPlayAnimation : BTNodeV2
    {
        public string AnimName;
        public bool WaitComplete = true;
        private bool _started;

        public BTPlayAnimation(string animName, bool waitComplete = true)
        {
            Name = $"PlayAnim({animName})"; AnimName = animName; WaitComplete = waitComplete;
        }

        public override BTNodeStatus Execute(BTContext context)
        {
            if (!_started)
            {
                context.OnPlayAnimation?.Invoke(AnimName);
                _started = true;
                if (!WaitComplete)
                {
                    Status = BTNodeStatus.Success;
                    return Status;
                }
            }
            // 检查动画是否播完
            if (context.IsAnimationFinished?.Invoke(AnimName) ?? true)
            {
                Status = BTNodeStatus.Success;
                _started = false;
                return Status;
            }
            Status = BTNodeStatus.Running;
            return Status;
        }

        public override void Reset() { _started = false; base.Reset(); }
    }

    // ========================================================================
    //  七、执行上下文
    // ========================================================================

    /// <summary>
    /// 行为树执行上下文。每次 tick 创建一个(或复用)。
    /// 包含黑板、agent 引用、delta time、各种回调。
    /// </summary>
    public sealed class BTContext
    {
        public Blackboard Blackboard;
        public float DeltaTime;
        public float ElapsedTime;
        public int AgentId;
        public object? AgentObject; // 任意用户数据

        // ── 兼容成员：ECS 引用与实体 ID（SmartObjects 等直接使用）──
        public YamaEngine.ECS.EntityComponentSystem? ECS;
        public int EntityId;

        // ── 导航系统引用（SmartObjects 等移动节点使用）──
        public NavigationSystem? Navigation;

        // 回调(由外部系统提供)
        public Func<Vector3>? GetAgentPositionFn;
        public Action<Vector3>? OnMoveRequested;
        public Action? OnMoveAborted;
        public Action<string>? OnPlayAnimation;
        public Func<string?, bool>? IsAnimationFinished;
        public Action<string>? OnBlackboardChanged;
        public Action<string>? LogWarning;
        public Action<string>? LogError;

        public Vector3 GetAgentPosition() => GetAgentPositionFn?.Invoke() ?? Vector3.Zero;

        public void Warn(string msg) => LogWarning?.Invoke(msg);
        public void Error(string msg) => LogError?.Invoke(msg);
    }

    // ========================================================================
    //  八、行为树主类
    // ========================================================================

    /// <summary>
    /// 行为树。一个 agent 一个实例，节点可以共享。
    /// </summary>
    public sealed class BehaviorTreeV2
    {
        public string Name = "";
        public BTNodeV2? Root;
        public Blackboard Blackboard = new();
        public bool IsActive = true;
        public float TickInterval = 0f; // 0=每帧 tick，>0=按间隔

        private float _tickTimer;
        private BTContext? _context;

        public BehaviorTreeV2() { }
        public BehaviorTreeV2(string name) { Name = name; }

        public void SetRoot(BTNodeV2 root)
        {
            Root = root;
            root.Parent = null;
        }

        /// <summary>初始化上下文。</summary>
        public void InitializeContext(int agentId, Func<Vector3> getPos)
        {
            _context = new BTContext
            {
                AgentId = agentId,
                Blackboard = Blackboard,
                GetAgentPositionFn = getPos
            };
        }

        /// <summary>外部设置回调。</summary>
        public void SetCallbacks(
            Action<Vector3>? onMove = null,
            Action? onMoveAbort = null,
            Action<string>? onPlayAnim = null,
            Func<string?, bool>? isAnimFinished = null)
        {
            if (_context == null) return;
            _context.OnMoveRequested = onMove;
            _context.OnMoveAborted = onMoveAbort;
            _context.OnPlayAnimation = onPlayAnim;
            _context.IsAnimationFinished = isAnimFinished;
        }

        /// <summary>每帧更新。</summary>
        public BTNodeStatus Tick(float dt)
        {
            if (!IsActive || Root == null || _context == null)
                return BTNodeStatus.NotStarted;

            _tickTimer += dt;
            if (TickInterval > 0 && _tickTimer < TickInterval)
                return Root.Status;

            _context.DeltaTime = _tickTimer > 0 ? _tickTimer : dt;
            _context.ElapsedTime += _context.DeltaTime;
            _tickTimer = 0;

            var status = Root.Execute(_context);
            return status;
        }

        /// <summary>中止当前执行。</summary>
        public void Abort()
        {
            if (Root != null && _context != null && Root.IsRunning)
                Root.OnAbort(_context);
        }

        /// <summary>重置整个树。</summary>
        public void Reset()
        {
            Root?.Reset();
            Blackboard.Clear();
            _tickTimer = 0;
        }
    }

    // ========================================================================
    //  九、BTBuilder (流式构建)
    // ========================================================================

    /// <summary>
    /// 流式 API 构建行为树。让代码更易读。
    /// 用法：
    ///   var tree = new BTBuilder(" PatrolTree ")
    ///       .Sequence()
    ///           .Task("PickWaypoint", ctx => {...})
    ///           .MoveTo(ctx => ctx.Blackboard.GetVector3("WP"))
    ///           .Wait(1f)
    ///       .End()
    ///       .Build();
    /// </summary>
    public sealed class BTBuilder
    {
        private readonly BehaviorTreeV2 _tree;
        private readonly Stack<BTNodeV2> _stack = new();

        public BTBuilder(string name = "")
        {
            _tree = new BehaviorTreeV2(name);
        }

        // Composite
        public BTBuilder Sequence()
        {
            var n = new BTSequence();
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder Selector()
        {
            var n = new BTSelector();
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder Parallel(BTParallelPolicy successPolicy = BTParallelPolicy.All, BTParallelPolicy failPolicy = BTParallelPolicy.Any)
        {
            var n = new BTParallel { SuccessPolicy = successPolicy, FailurePolicy = failPolicy };
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder RandomSelector()
        {
            var n = new BTRandomSelector();
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        // Decorator
        public BTBuilder Condition(Func<BTContext, bool> cond)
        {
            var n = new BTCondition(cond);
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder Inverter()
        {
            var n = new BTInverter();
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder Repeater(int count = -1, bool repeatOnFail = false)
        {
            var n = new BTRepeater(null, count) { RepeatOnFailure = repeatOnFail };
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder Timeout(float seconds)
        {
            var n = new BTTimeout(seconds);
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder CoolDown(float seconds)
        {
            var n = new BTCoolDown(seconds);
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder BlackboardCheck(string key, BlackboardKeyType type, BTBlackboardCheck.ComparisonOp op, object? expected = null)
        {
            var n = new BTBlackboardCheck(key, type, op, expected);
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        public BTBuilder UntilSuccess(int maxAttempts = -1)
        {
            var n = new BTUntilSuccess(null, maxAttempts);
            AddChild(n);
            _stack.Push(n);
            return this;
        }

        // Tasks
        public BTBuilder Task(string name, Func<BTContext, BTNodeStatus> task)
        {
            AddChild(new BTTask(name, task));
            return this;
        }

        public BTBuilder Wait(float seconds)
        {
            AddChild(new BTWait(seconds));
            return this;
        }

        public BTBuilder WaitForCondition(Func<BTContext, bool> cond, float timeout = -1)
        {
            AddChild(new BTWaitForCondition(cond) { Timeout = timeout });
            return this;
        }

        public BTBuilder MoveTo(Func<BTContext, Vector3> getTarget, float acceptance = 0.5f)
        {
            AddChild(new BTMoveTo(getTarget, acceptance));
            return this;
        }

        public BTBuilder PlayAnimation(string animName, bool waitComplete = true)
        {
            AddChild(new BTPlayAnimation(animName, waitComplete));
            return this;
        }

        public BTBuilder NoOp()
        {
            AddChild(new BTNoOp());
            return this;
        }

        // Control
        public BTBuilder End()
        {
            _stack.Pop();
            return this;
        }

        public BTBuilder Spawn(out BTNodeV2 node)
        {
            node = _stack.Count > 0 ? _stack.Peek() : null!;
            return this;
        }

        public BehaviorTreeV2 Build()
        {
            if (_stack.Count > 0)
                throw new InvalidOperationException("BTBuilder 还有未 End 的节点");
            if (_tree.Root == null)
                throw new InvalidOperationException("没添加任何节点");
            return _tree;
        }

        private void AddChild(BTNodeV2 node)
        {
            if (_stack.Count == 0)
            {
                _tree.SetRoot(node);
            }
            else
            {
                var parent = _stack.Peek();
                if (parent is BTDecoratorV2 dec)
                    dec.SetChild(node);
                else
                {
                    parent.Children.Add(node);
                    node.Parent = parent;
                }
            }
        }
    }

    // ========================================================================
    //  十、行为树管理器(多 agent)
    // ========================================================================

    /// <summary>
    /// 管理多个 agent 的行为树。统一 tick 调度。
    /// </summary>
}
