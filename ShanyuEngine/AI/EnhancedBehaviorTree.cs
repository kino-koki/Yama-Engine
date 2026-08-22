// ============================================================================
//  ShanyuEngine.AI.EnhancedBehaviorTree.cs
//  山雨引擎 -- 增强行为树系统 (Enhanced Behavior Tree)
//  对齐 UE5 BehaviorTree / Behavior Designer / NodeCanvas
// ----------------------------------------------------------------------------
//  本文件实现一套自包含的增强行为树 (EBT 前缀避免与 BehaviorTreeV2 冲突)：
//    1. EBTBlackboard      - 类型安全黑板 (泛型访问/观察者/元数据)
//    2. EBTNode 基类       - 抽象节点 + 状态 + 活动节点追踪
//    3. 组合节点           - Sequence/Selector/Parallel/Priority/Random/Weighted
//    4. 装饰器节点         - Repeater/Retry/Inverter/Timeout/Throttle/Cooldown
//    5. 条件节点           - HasTarget/InAttackRange/IsLowHealth/CanSeeEnemy/IsDayTime/CustomPredicate
//    6. 动作节点           - MoveToTarget/Attack/Dodge/Flee/Patrol/Investigate/Interact/CastAbility
//    7. EBTContext         - 执行上下文 (黑板/回调/时间)
//    8. EnhancedBehaviorTree - 主类 (根/Tick/热重载/调试)
//    9. EBTSerializer      - JSON 序列化 -> 运行时重建 (热重载)
//   10. GetActiveNodes()   - 调试可视化接口
// ----------------------------------------------------------------------------
//  与现有 BehaviorTreeV2 并存，可通过适配层互转；零 NotImplementedException。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、执行状态与黑板
    // ========================================================================

    /// <summary>增强行为树节点状态。</summary>
    public enum EBTStatus
    {
        Success,
        Failure,
        Running,
        NotStarted,
        Aborted
    }

    /// <summary>
    /// 增强黑板：键值对存储 AI 状态，支持类型安全的泛型访问与变更观察者。
    /// </summary>
    public sealed class EBTBlackboard
    {
        private readonly Dictionary<string, object?> _values = new();
        private readonly Dictionary<string, Type> _types = new();
        private readonly Dictionary<string, List<Action>> _observers = new();

        public int Count => _values.Count;
        public IEnumerable<string> Keys => _values.Keys;

        /// <summary>类型安全写入。</summary>
        public void SetValue<T>(string key, T value)
        {
            _values[key] = value;
            _types[key] = typeof(T);
            Notify(key);
        }

        /// <summary>类型安全读取 (带默认值)。</summary>
        public T GetValue<T>(string key, T defaultValue = default!)
        {
            if (_values.TryGetValue(key, out var v) && v is T t) return t;
            return defaultValue;
        }

        public bool TryGetValue<T>(string key, out T value)
        {
            if (_values.TryGetValue(key, out var v) && v is T t) { value = t; return true; }
            value = default!;
            return false;
        }

        public bool HasKey(string key) => _values.ContainsKey(key);
        public Type? GetKeyType(string key) => _types.TryGetValue(key, out var t) ? t : null;
        public void RemoveKey(string key) { _values.Remove(key); _types.Remove(key); Notify(key); }

        public void Observe(string key, Action callback)
        {
            if (!_observers.TryGetValue(key, out var list)) _observers[key] = list = new List<Action>();
            list.Add(callback);
        }
        public void StopObserve(string key, Action callback)
        {
            if (_observers.TryGetValue(key, out var list)) list.Remove(callback);
        }
        private void Notify(string key)
        {
            if (_observers.TryGetValue(key, out var list))
                for (int i = 0; i < list.Count; i++) list[i]();
        }

        public void Clear() { _values.Clear(); _types.Clear(); _observers.Clear(); }

        public EBTBlackboard Clone()
        {
            var c = new EBTBlackboard();
            foreach (var kv in _values) { c._values[kv.Key] = kv.Value; c._types[kv.Key] = _types[kv.Key]; }
            return c;
        }
    }

    // ========================================================================
    //  二、节点基类
    // ========================================================================

    /// <summary>增强行为树节点基类。</summary>
    public abstract class EBTNode
    {
        public string Name = "";
        public EBTStatus Status = EBTStatus.NotStarted;
        public EBTNode? Parent;
        public readonly List<EBTNode> Children = new();
        public bool IsRunning => Status == EBTStatus.Running;

        public abstract EBTStatus Execute(EBTContext ctx);
        public virtual void OnAbort(EBTContext ctx) { }
        public virtual void Reset()
        {
            Status = EBTStatus.NotStarted;
            for (int i = 0; i < Children.Count; i++) Children[i].Reset();
        }
        public EBTNode SetName(string n) { Name = n; return this; }
        public override string ToString() => $"{GetType().Name} '{Name}' [{Status}]";
    }

    // ========================================================================
    //  三、组合节点
    // ========================================================================

    /// <summary>顺序节点：全部成功才成功，任一失败即失败。</summary>
    public sealed class EBTSequence : EBTNode
    {
        private int _idx;
        public EBTSequence() { Name = "Sequence"; }
        public EBTSequence(params EBTNode[] c) { Name = "Sequence"; Children.AddRange(c); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            while (_idx < Children.Count)
            {
                var s = Children[_idx].Execute(ctx);
                if (s == EBTStatus.Running) { Status = s; return s; }
                if (s == EBTStatus.Failure) { _idx = 0; Status = s; return s; }
                _idx++;
            }
            _idx = 0; Status = EBTStatus.Success; return Status;
        }
        public override void Reset() { _idx = 0; base.Reset(); }
    }

    /// <summary>选择节点：任一成功即成功，全部失败才失败。</summary>
    public sealed class EBTSelector : EBTNode
    {
        private int _idx;
        public EBTSelector() { Name = "Selector"; }
        public EBTSelector(params EBTNode[] c) { Name = "Selector"; Children.AddRange(c); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            while (_idx < Children.Count)
            {
                var s = Children[_idx].Execute(ctx);
                if (s == EBTStatus.Running) { Status = s; return s; }
                if (s == EBTStatus.Success) { _idx = 0; Status = s; return s; }
                _idx++;
            }
            _idx = 0; Status = EBTStatus.Failure; return Status;
        }
        public override void Reset() { _idx = 0; base.Reset(); }
    }

    /// <summary>并行节点：同时执行所有子节点。成功/失败策略可配置。</summary>
    public enum EBTParallelPolicy { All, Any }
    public sealed class EBTParallel : EBTNode
    {
        public EBTParallelPolicy SuccessPolicy = EBTParallelPolicy.All;
        public EBTParallelPolicy FailurePolicy = EBTParallelPolicy.Any;
        public EBTParallel() { Name = "Parallel"; }
        public EBTParallel(params EBTNode[] c) { Name = "Parallel"; Children.AddRange(c); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            int ok = 0, fail = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                var s = Children[i].Execute(ctx);
                if (s == EBTStatus.Success) ok++;
                else if (s == EBTStatus.Failure) fail++;
            }
            bool success = SuccessPolicy == EBTParallelPolicy.All ? ok == Children.Count : ok > 0;
            bool failure = FailurePolicy == EBTParallelPolicy.All ? fail == Children.Count : fail > 0;
            if (failure) { Status = EBTStatus.Failure; return Status; }
            if (success) { Status = EBTStatus.Success; return Status; }
            Status = EBTStatus.Running; return Status;
        }
    }

    /// <summary>优先级节点：子节点带优先级，按优先级从高到低尝试 (高优先级 Running 时抢占低优先级)。</summary>
    public sealed class EBTPriority : EBTNode
    {
        public List<int> Priorities = new();
        private int _active = -1;
        public EBTPriority() { Name = "Priority"; }

        public void AddChild(EBTNode child, int priority)
        {
            Children.Add(child); Priorities.Add(priority);
        }

        public override EBTStatus Execute(EBTContext ctx)
        {
            // 按优先级降序排索引
            var order = new int[Children.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => Priorities[b].CompareTo(Priorities[a]));

            for (int oi = 0; oi < order.Length; oi++)
            {
                int i = order[oi];
                var s = Children[i].Execute(ctx);
                if (s == EBTStatus.Success) { AbortLower(ctx, i, order, oi); _active = i; Status = s; return s; }
                if (s == EBTStatus.Running) { AbortLower(ctx, i, order, oi); _active = i; Status = s; return s; }
                // failure: 继续尝试下一优先级
            }
            _active = -1; Status = EBTStatus.Failure; return Status;
        }

        private void AbortLower(EBTContext ctx, int current, int[] order, int currentOrderIdx)
        {
            for (int oi = currentOrderIdx + 1; oi < order.Length; oi++)
            {
                int i = order[oi];
                if (Children[i].IsRunning) { Children[i].OnAbort(ctx); Children[i].Reset(); }
            }
        }
        public override void Reset() { _active = -1; base.Reset(); }
    }

    /// <summary>随机选择节点：打乱顺序后当 Selector。</summary>
    public sealed class EBTRandom : EBTNode
    {
        private int _idx;
        private int[]? _order;
        private readonly Random _rng = new();
        public EBTRandom() { Name = "Random"; }
        public EBTRandom(params EBTNode[] c) { Name = "Random"; Children.AddRange(c); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            if (_order == null)
            {
                _order = new int[Children.Count];
                for (int i = 0; i < _order.Length; i++) _order[i] = i;
                for (int i = _order.Length - 1; i > 0; i--) { int j = _rng.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); }
                _idx = 0;
            }
            while (_idx < _order.Length)
            {
                var s = Children[_order[_idx]].Execute(ctx);
                if (s == EBTStatus.Running) { Status = s; return s; }
                if (s == EBTStatus.Success) { _order = null; Status = s; return s; }
                _idx++;
            }
            _order = null; Status = EBTStatus.Failure; return Status;
        }
        public override void Reset() { _idx = 0; _order = null; base.Reset(); }
    }

    /// <summary>权重随机节点：按权重随机选一个子节点执行。</summary>
    public sealed class EBTWeighted : EBTNode
    {
        public List<float> Weights = new();
        private int _picked = -1;
        private readonly Random _rng = new();
        public EBTWeighted() { Name = "Weighted"; }
        public void AddChild(EBTNode child, float weight) { Children.Add(child); Weights.Add(weight); }

        public override EBTStatus Execute(EBTContext ctx)
        {
            if (_picked < 0) _picked = Pick();
            if (_picked < 0 || _picked >= Children.Count) { Status = EBTStatus.Failure; _picked = -1; return Status; }
            var s = Children[_picked].Execute(ctx);
            if (s != EBTStatus.Running) _picked = -1;
            Status = s; return s;
        }
        private int Pick()
        {
            float total = 0; for (int i = 0; i < Weights.Count && i < Children.Count; i++) total += MathF.Max(0, Weights[i]);
            if (total <= 0) return Children.Count > 0 ? 0 : -1;
            float r = (float)_rng.NextDouble() * total, acc = 0;
            for (int i = 0; i < Weights.Count && i < Children.Count; i++)
            {
                acc += MathF.Max(0, Weights[i]);
                if (r <= acc) return i;
            }
            return Children.Count - 1;
        }
        public override void Reset() { _picked = -1; base.Reset(); }
    }

    // ========================================================================
    //  四、装饰器节点
    // ========================================================================

    public abstract class EBTDecorator : EBTNode
    {
        public EBTNode? Child;
        protected void SetChild(EBTNode c) { Child = c; Children.Clear(); Children.Add(c); c.Parent = this; }
    }

    /// <summary>重复装饰器：子节点成功后重复 N 次 (-1=无限)，失败可选继续。</summary>
    public sealed class EBTRepeater : EBTDecorator
    {
        public int Count = -1;
        public bool RepeatOnFailure;
        private int _done;
        public EBTRepeater(EBTNode? child = null, int count = -1) { Name = "Repeater"; if (child != null) SetChild(child); Count = count; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            while (true)
            {
                var s = Child!.Execute(ctx);
                if (s == EBTStatus.Running) { Status = s; return s; }
                if (s == EBTStatus.Failure && !RepeatOnFailure) { _done = 0; Status = s; return s; }
                _done++;
                if (Count > 0 && _done >= Count) { _done = 0; Status = EBTStatus.Success; return Status; }
                Child.Reset();
            }
        }
        public override void Reset() { _done = 0; base.Reset(); }
    }

    /// <summary>重试装饰器：子节点失败时重试，直到成功或达到最大次数。</summary>
    public sealed class EBTRetry : EBTDecorator
    {
        public int MaxAttempts = 3;
        private int _attempts;
        public EBTRetry(EBTNode? child = null, int max = 3) { Name = "Retry"; if (child != null) SetChild(child); MaxAttempts = max; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            while (true)
            {
                var s = Child!.Execute(ctx);
                if (s != EBTStatus.Failure) { _attempts = 0; Status = s; return s; }
                _attempts++;
                if (_attempts >= MaxAttempts) { _attempts = 0; Status = EBTStatus.Failure; return Status; }
                Child.Reset();
            }
        }
        public override void Reset() { _attempts = 0; base.Reset(); }
    }

    /// <summary>取反装饰器：Success<->Failure 互换。</summary>
    public sealed class EBTInverter : EBTDecorator
    {
        public EBTInverter(EBTNode? child = null) { Name = "Inverter"; if (child != null) SetChild(child); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            var s = Child!.Execute(ctx);
            Status = s == EBTStatus.Success ? EBTStatus.Failure : s == EBTStatus.Failure ? EBTStatus.Success : s;
            return Status;
        }
    }

    /// <summary>超时装饰器：超过时长则中止并失败。</summary>
    public sealed class EBTTimeout : EBTDecorator
    {
        public float Seconds;
        private float _elapsed;
        public EBTTimeout(float seconds, EBTNode? child = null) { Name = "Timeout"; if (child != null) SetChild(child); Seconds = seconds; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            _elapsed += ctx.DeltaTime;
            if (_elapsed >= Seconds) { Child!.OnAbort(ctx); _elapsed = 0; Status = EBTStatus.Failure; return Status; }
            var s = Child!.Execute(ctx);
            if (s != EBTStatus.Running) _elapsed = 0;
            Status = s; return s;
        }
        public override void Reset() { _elapsed = 0; base.Reset(); }
    }

    /// <summary>节流装饰器：限制子节点的执行频率 (每 Interval 秒最多执行一次)。</summary>
    public sealed class EBTThrottle : EBTDecorator
    {
        public float Interval;
        private float _timer;
        public EBTThrottle(float interval, EBTNode? child = null) { Name = "Throttle"; if (child != null) SetChild(child); Interval = interval; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            _timer -= ctx.DeltaTime;
            if (_timer > 0) { Status = EBTStatus.Running; return Status; } // 节流期间视为运行中(挂起)
            var s = Child!.Execute(ctx);
            if (s != EBTStatus.Running) _timer = Interval;
            Status = s; return s;
        }
        public override void Reset() { _timer = 0; base.Reset(); }
    }

    /// <summary>冷却装饰器：子节点完成后强制等待一段时间才能再次执行。</summary>
    public sealed class EBTCooldown : EBTDecorator
    {
        public float Seconds;
        private float _timer;
        public EBTCooldown(float seconds, EBTNode? child = null) { Name = "Cooldown"; if (child != null) SetChild(child); Seconds = seconds; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            if (_timer > 0) { _timer -= ctx.DeltaTime; Status = EBTStatus.Failure; return Status; }
            var s = Child!.Execute(ctx);
            if (s == EBTStatus.Success || s == EBTStatus.Failure) _timer = Seconds;
            Status = s; return s;
        }
        public override void Reset() { _timer = 0; base.Reset(); }
    }

    // ========================================================================
    //  五、条件节点
    // ========================================================================

    public abstract class EBTCondition : EBTNode
    {
        public abstract bool Check(EBTContext ctx);
        public override EBTStatus Execute(EBTContext ctx)
        {
            Status = Check(ctx) ? EBTStatus.Success : EBTStatus.Failure;
            return Status;
        }
    }

    public sealed class EBTHasTarget : EBTCondition { public EBTHasTarget() { Name = "HasTarget"; } public override bool Check(EBTContext ctx) => ctx.HasTarget(); }
    public sealed class EBTInAttackRange : EBTCondition
    {
        public float Range = 2f;
        public EBTInAttackRange(float range = 2f) { Name = "InAttackRange"; Range = range; }
        public override bool Check(EBTContext ctx) => ctx.DistanceToTarget() <= Range;
    }
    public sealed class EBTIsLowHealth : EBTCondition
    {
        public float Threshold = 0.3f;
        public EBTIsLowHealth(float threshold = 0.3f) { Name = "IsLowHealth"; Threshold = threshold; }
        public override bool Check(EBTContext ctx) => ctx.GetHealthRatio() <= Threshold;
    }
    public sealed class EBTCanSeeEnemy : EBTCondition { public EBTCanSeeEnemy() { Name = "CanSeeEnemy"; } public override bool Check(EBTContext ctx) => ctx.Blackboard.GetValue("CanSeeEnemy", false); }
    public sealed class EBTIsDayTime : EBTCondition
    {
        public EBTIsDayTime() { Name = "IsDayTime"; }
        public override bool Check(EBTContext ctx)
        {
            // 黑板可写入世界时间 (0~24)；默认按 6~18 为白天
            float t = ctx.Blackboard.GetValue("WorldTime", 12f);
            return t >= 6f && t < 18f;
        }
    }
    public sealed class EBTCustomPredicate : EBTCondition
    {
        public Func<EBTContext, bool> Predicate;
        public EBTCustomPredicate(Func<EBTContext, bool> pred) { Name = "CustomPredicate"; Predicate = pred; }
        public override bool Check(EBTContext ctx) => Predicate(ctx);
    }

    // ========================================================================
    //  六、动作节点
    // ========================================================================

    public abstract class EBTAction : EBTNode { }

    /// <summary>移动到目标：持续运行直到进入接受半径。</summary>
    public sealed class EBTMoveToTarget : EBTAction
    {
        public float Acceptance = 0.8f;
        public EBTMoveToTarget(float acceptance = 0.8f) { Name = "MoveToTarget"; Acceptance = acceptance; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            if (!ctx.HasTarget()) { Status = EBTStatus.Failure; return Status; }
            ctx.OnMoveTo?.Invoke(ctx.GetTargetPosition());
            if (ctx.DistanceToTarget() <= Acceptance) { Status = EBTStatus.Success; return Status; }
            Status = EBTStatus.Running; return Status;
        }
        public override void OnAbort(EBTContext ctx) => ctx.OnMoveAborted?.Invoke();
    }

    public sealed class EBTAttack : EBTAction
    {
        public EBTAttack() { Name = "Attack"; }
        public override EBTStatus Execute(EBTContext ctx) { ctx.OnAttack?.Invoke(); Status = EBTStatus.Success; return Status; }
    }
    public sealed class EBTDodge : EBTAction
    {
        public Vector3 Direction = Vector3.UnitX;
        public EBTDodge(Vector3 dir = default) { Name = "Dodge"; if (dir != default) Direction = Vector3.Normalize(dir); }
        public override EBTStatus Execute(EBTContext ctx) { ctx.OnDodge?.Invoke(Direction); Status = EBTStatus.Success; return Status; }
    }
    /// <summary>逃跑：远离目标直到距离超过安全距离。</summary>
    public sealed class EBTFlee : EBTAction
    {
        public float SafeDistance = 15f;
        public EBTFlee(float safe = 15f) { Name = "Flee"; SafeDistance = safe; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            var pos = ctx.GetAgentPosition();
            if (!ctx.HasTarget()) { Status = EBTStatus.Success; return Status; }
            var target = ctx.GetTargetPosition();
            var away = pos - target;
            if (away.LengthSquared() < 1e-4f) away = Vector3.UnitX;
            away = Vector3.Normalize(away);
            ctx.OnMoveTo?.Invoke(pos + away * SafeDistance);
            if (Vector3.Distance(pos, target) >= SafeDistance) { Status = EBTStatus.Success; return Status; }
            Status = EBTStatus.Running; return Status;
        }
    }
    /// <summary>巡逻：在巡逻点之间循环移动。</summary>
    public sealed class EBTPatrol : EBTAction
    {
        public List<Vector3> Points = new();
        private int _cur;
        public EBTPatrol(IEnumerable<Vector3> points) { Name = "Patrol"; Points.AddRange(points); }
        public override EBTStatus Execute(EBTContext ctx)
        {
            if (Points.Count == 0) { Status = EBTStatus.Failure; return Status; }
            var target = Points[_cur];
            ctx.OnMoveTo?.Invoke(target);
            if (Vector3.Distance(ctx.GetAgentPosition(), target) <= 1f) _cur = (_cur + 1) % Points.Count;
            Status = EBTStatus.Running; return Status;
        }
    }
    /// <summary>调查：移动到黑板记录的最后感知位置。</summary>
    public sealed class EBTInvestigate : EBTAction
    {
        public EBTInvestigate() { Name = "Investigate"; }
        public override EBTStatus Execute(EBTContext ctx)
        {
            var pt = ctx.Blackboard.GetValue("InvestigatePosition", Vector3.Zero);
            ctx.OnMoveTo?.Invoke(pt);
            if (Vector3.Distance(ctx.GetAgentPosition(), pt) <= 1.5f) { Status = EBTStatus.Success; return Status; }
            Status = EBTStatus.Running; return Status;
        }
    }
    public sealed class EBTInteract : EBTAction
    {
        public string TargetTag = "";
        public EBTInteract(string tag = "") { Name = "Interact"; TargetTag = tag; }
        public override EBTStatus Execute(EBTContext ctx) { ctx.OnInteract?.Invoke(TargetTag); Status = EBTStatus.Success; return Status; }
    }
    public sealed class EBTCastAbility : EBTAction
    {
        public int AbilityId;
        public EBTCastAbility(int abilityId) { Name = "CastAbility"; AbilityId = abilityId; }
        public override EBTStatus Execute(EBTContext ctx) { ctx.OnCastAbility?.Invoke(AbilityId); Status = EBTStatus.Success; return Status; }
    }

    // ========================================================================
    //  七、执行上下文
    // ========================================================================

    public sealed class EBTContext
    {
        public EBTBlackboard Blackboard = new();
        public float DeltaTime;
        public float ElapsedTime;
        public int AgentId;

        // 数据提供回调
        public Func<Vector3>? GetAgentPositionFn;
        public Func<Vector3>? GetTargetPositionFn;
        public Func<bool>? HasTargetFn;
        public Func<float>? GetHealthRatioFn;

        // 动作回调
        public Action<Vector3>? OnMoveTo;
        public Action? OnMoveAborted;
        public Action? OnAttack;
        public Action<Vector3>? OnDodge;
        public Action<string>? OnInteract;
        public Action<int>? OnCastAbility;

        public Vector3 GetAgentPosition() => GetAgentPositionFn?.Invoke() ?? Vector3.Zero;
        public Vector3 GetTargetPosition() => GetTargetPositionFn?.Invoke() ?? Vector3.Zero;
        public bool HasTarget() => HasTargetFn?.Invoke() ?? false;
        public float GetHealthRatio() => GetHealthRatioFn?.Invoke() ?? 1f;
        public float DistanceToTarget() => HasTarget() ? Vector3.Distance(GetAgentPosition(), GetTargetPosition()) : float.MaxValue;
    }

    // ========================================================================
    //  八、增强行为树主类
    // ========================================================================

    public sealed class EnhancedBehaviorTree
    {
        public string Name = "";
        public EBTNode? Root;
        public EBTBlackboard Blackboard = new();
        public bool IsActive = true;
        public float TickInterval; // 0=每帧
        private float _timer;
        private EBTContext? _ctx;

        public EnhancedBehaviorTree() { }
        public EnhancedBehaviorTree(string name) { Name = name; }
        public void SetRoot(EBTNode root) { Root = root; root.Parent = null; }

        public void InitializeContext(int agentId, Func<Vector3> getPos)
        {
            _ctx = new EBTContext { AgentId = agentId, Blackboard = Blackboard, GetAgentPositionFn = getPos };
        }
        public EBTContext? Context => _ctx;
        public void SetContext(EBTContext ctx) { _ctx = ctx; _ctx.Blackboard = Blackboard; }

        public EBTStatus Tick(float dt)
        {
            if (!IsActive || Root == null || _ctx == null) return EBTStatus.NotStarted;
            _timer += dt;
            if (TickInterval > 0 && _timer < TickInterval) return Root.Status;
            _ctx.DeltaTime = _timer > 0 ? _timer : dt;
            _ctx.ElapsedTime += _ctx.DeltaTime;
            _timer = 0;
            return Root.Execute(_ctx);
        }

        public void Abort() { if (Root != null && _ctx != null && Root.IsRunning) Root.OnAbort(_ctx); }
        public void Reset() { Root?.Reset(); }

        // ── 调试可视化接口 ──────────────────────────────────────────────
        /// <summary>获取当前活动 (Running) 的节点及其路径，供编辑器高亮。</summary>
        public List<(EBTNode node, string path)> GetActiveNodes()
        {
            var result = new List<(EBTNode, string)>();
            if (Root != null) CollectActive(Root, "", result);
            return result;
        }
        private static void CollectActive(EBTNode node, string path, List<(EBTNode, string)> result)
        {
            string p = string.IsNullOrEmpty(path) ? node.Name : path + "/" + node.Name;
            if (node.IsRunning) result.Add((node, p));
            for (int i = 0; i < node.Children.Count; i++) CollectActive(node.Children[i], p, result);
        }

        // ── 热重载 ──────────────────────────────────────────────────────
        /// <summary>序列化为 JSON (用于热重载/存档)。</summary>
        public string Serialize() => EBTSerializer.Serialize(this);
        /// <summary>从 JSON 热重载：重建树结构并替换根节点 (保留黑板)。</summary>
        public bool HotReload(string json, Func<string, EBTNode?>? customNodeFactory = null)
        {
            var newRoot = EBTSerializer.Deserialize(json, customNodeFactory);
            if (newRoot == null) return false;
            Root = newRoot;
            Root.Reset();
            if (_ctx != null) _ctx.Blackboard = Blackboard;
            return true;
        }
    }

    // ========================================================================
    //  九、序列化 / 热重载 (JSON)
    // ========================================================================

    /// <summary>可序列化的节点描述 (POCO)。</summary>
    public sealed class EBTNodeData
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, string> Params { get; set; } = new();
        public List<EBTNodeData> Children { get; set; } = new();
    }

    /// <summary>行为树 JSON 序列化器：将树结构序列化为可重建的 JSON，支持运行时热重载。</summary>
    public static class EBTSerializer
    {
        private static readonly JsonSerializerOptions _opt = new() { WriteIndented = true };

        public static string Serialize(EnhancedBehaviorTree tree)
        {
            var data = new EBTNodeData { Type = "Tree", Name = tree.Name, Children = new List<EBTNodeData>() };
            if (tree.Root != null) data.Children.Add(ToData(tree.Root));
            return JsonSerializer.Serialize(data, _opt);
        }

        private static EBTNodeData ToData(EBTNode node)
        {
            var d = new EBTNodeData { Type = node.GetType().Name, Name = node.Name };
            // 节点特有参数
            switch (node)
            {
                case EBTTimeout t: d.Params["seconds"] = t.Seconds.ToString(CultureInfo.InvariantCulture); break;
                case EBTThrottle t: d.Params["interval"] = t.Interval.ToString(CultureInfo.InvariantCulture); break;
                case EBTCooldown t: d.Params["seconds"] = t.Seconds.ToString(CultureInfo.InvariantCulture); break;
                case EBTRepeater r: d.Params["count"] = r.Count.ToString(CultureInfo.InvariantCulture); d.Params["repeatOnFailure"] = r.RepeatOnFailure.ToString(); break;
                case EBTRetry r: d.Params["maxAttempts"] = r.MaxAttempts.ToString(CultureInfo.InvariantCulture); break;
                case EBTInAttackRange c: d.Params["range"] = c.Range.ToString(CultureInfo.InvariantCulture); break;
                case EBTIsLowHealth c: d.Params["threshold"] = c.Threshold.ToString(CultureInfo.InvariantCulture); break;
                case EBTPriority p: for (int i = 0; i < p.Priorities.Count; i++) d.Params["pri_" + i] = p.Priorities[i].ToString(CultureInfo.InvariantCulture); break;
                case EBTWeighted w: for (int i = 0; i < w.Weights.Count; i++) d.Params["w_" + i] = w.Weights[i].ToString(CultureInfo.InvariantCulture); break;
                case EBTMoveToTarget m: d.Params["acceptance"] = m.Acceptance.ToString(CultureInfo.InvariantCulture); break;
                case EBTFlee f: d.Params["safeDistance"] = f.SafeDistance.ToString(CultureInfo.InvariantCulture); break;
                case EBTCastAbility c: d.Params["abilityId"] = c.AbilityId.ToString(CultureInfo.InvariantCulture); break;
                case EBTInteract c: d.Params["targetTag"] = c.TargetTag; break;
            }
            // 装饰器的单个子节点
            if (node is EBTDecorator dec && dec.Child != null) d.Children.Add(ToData(dec.Child));
            else for (int i = 0; i < node.Children.Count; i++) d.Children.Add(ToData(node.Children[i]));
            return d;
        }

        public static EBTNode? Deserialize(string json, Func<string, EBTNode?>? customFactory = null)
        {
            try
            {
                var data = JsonSerializer.Deserialize<EBTNodeData>(json);
                if (data == null) return null;
                // 顶层为 Tree 包装
                if (data.Type == "Tree" && data.Children.Count > 0) data = data.Children[0];
                return BuildNode(data, customFactory);
            }
            catch { return null; }
        }

        private static EBTNode? BuildNode(EBTNodeData d, Func<string, EBTNode?>? customFactory)
        {
            EBTNode? node = CreateByType(d, customFactory);
            if (node == null) return null;
            node.Name = string.IsNullOrEmpty(d.Name) ? node.Name : d.Name;
            // 组合节点添加子节点
            if (node is EBTDecorator dec)
            {
                if (d.Children.Count > 0)
                {
                    var child = BuildNode(d.Children[0], customFactory);
                    if (child != null) dec.GetType().GetMethod("SetChild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                        ?.Invoke(dec, new object[] { child });
                }
            }
            else if (node is EBTPriority pri)
            {
                for (int i = 0; i < d.Children.Count; i++)
                {
                    var c = BuildNode(d.Children[i], customFactory);
                    if (c != null) pri.AddChild(c, d.Params.TryGetValue("pri_" + i, out var pv) && int.TryParse(pv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priVal) ? priVal : 0);
                }
            }
            else if (node is EBTWeighted w)
            {
                for (int i = 0; i < d.Children.Count; i++)
                {
                    var c = BuildNode(d.Children[i], customFactory);
                    if (c != null) w.AddChild(c, d.Params.TryGetValue("w_" + i, out var wv) && float.TryParse(wv, NumberStyles.Float, CultureInfo.InvariantCulture, out var wVal) ? wVal : 1f);
                }
            }
            else
            {
                for (int i = 0; i < d.Children.Count; i++)
                {
                    var c = BuildNode(d.Children[i], customFactory);
                    if (c != null) { node.Children.Add(c); c.Parent = node; }
                }
            }
            return node;
        }

        private static bool P(EBTNodeData d, string key, out float v) => float.TryParse(d.Params.GetValueOrDefault(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        private static bool P(EBTNodeData d, string key, out int v) => int.TryParse(d.Params.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        private static EBTNode? CreateByType(EBTNodeData d, Func<string, EBTNode?>? customFactory)
        {
            switch (d.Type)
            {
                case "EBTSequence": return new EBTSequence();
                case "EBTSelector": return new EBTSelector();
                case "EBTParallel": return new EBTParallel();
                case "EBTPriority": return new EBTPriority();
                case "EBTRandom": return new EBTRandom();
                case "EBTWeighted": return new EBTWeighted();
                case "EBTRepeater": P(d, "count", out int rc); return new EBTRepeater(null, rc) { RepeatOnFailure = d.Params.GetValueOrDefault("repeatOnFailure") == "True" };
                case "EBTRetry": P(d, "maxAttempts", out int ma); return new EBTRetry(null, ma);
                case "EBTInverter": return new EBTInverter();
                case "EBTTimeout": P(d, "seconds", out float ts); return new EBTTimeout(ts);
                case "EBTThrottle": P(d, "interval", out float ti); return new EBTThrottle(ti);
                case "EBTCooldown": P(d, "seconds", out float cs); return new EBTCooldown(cs);
                case "EBTHasTarget": return new EBTHasTarget();
                case "EBTInAttackRange": P(d, "range", out float rr); return new EBTInAttackRange(rr);
                case "EBTIsLowHealth": P(d, "threshold", out float th); return new EBTIsLowHealth(th);
                case "EBTCanSeeEnemy": return new EBTCanSeeEnemy();
                case "EBTIsDayTime": return new EBTIsDayTime();
                case "EBTMoveToTarget": P(d, "acceptance", out float ac); return new EBTMoveToTarget(ac);
                case "EBTAttack": return new EBTAttack();
                case "EBTDodge": return new EBTDodge();
                case "EBTFlee": P(d, "safeDistance", out float sd); return new EBTFlee(sd);
                case "EBTPatrol": return new EBTPatrol(Array.Empty<Vector3>());
                case "EBTInvestigate": return new EBTInvestigate();
                case "EBTInteract": return new EBTInteract(d.Params.GetValueOrDefault("targetTag", ""));
                case "EBTCastAbility": P(d, "abilityId", out int aid); return new EBTCastAbility(aid);
                default: return customFactory?.Invoke(d.Type);
            }
        }
    }
}
