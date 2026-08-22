using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.AI;

public sealed class BehaviorTreeBuilder
{
    private readonly BehaviorTree _tree = new(); private BehaviorNode? _current;

    public BehaviorTreeBuilder Selector(string name = "Selector") { var n = new SelectorNode(name); Attach(n); return this; }
    public BehaviorTreeBuilder Sequence(string name = "Sequence") { var n = new SequenceNode(name); Attach(n); return this; }
    public BehaviorTreeBuilder Parallel(string name = "Parallel", int requiredToSucceed = -1) { var n = new ParallelNode(name, requiredToSucceed); Attach(n); return this; }

    public BehaviorTreeBuilder Condition(string name, Func<bool> condition)
    {
        var n = new ConditionNode(name, condition); Attach(n); if (_current == n) _current = null; return this;
    }

    public BehaviorTreeBuilder Action(string name, Func<BehaviorStatus> action)
    {
        var n = new ActionNode(name, action); Attach(n); if (_current == n) _current = null; return this;
    }

    public BehaviorTreeBuilder Wait(float seconds) => Action("Wait", () => { _waitTimer += Time.deltaTime; if (_waitTimer >= seconds) { _waitTimer = 0; return BehaviorStatus.Success; } return BehaviorStatus.Running; });
    private float _waitTimer;

    public BehaviorTreeBuilder Log(string msg) => Action("Log", () => { Console.WriteLine(msg); return BehaviorStatus.Success; });

    public BehaviorTreeBuilder Invert(string name = "Invert")
    {
        var n = new DecoratorNode(name,
            (child) => {
                var s = child.Evaluate();
                if (s == BehaviorStatus.Success) return BehaviorStatus.Failure;
                if (s == BehaviorStatus.Failure) return BehaviorStatus.Success;
                return s;
            });
        Attach(n); return this;
    }

    public BehaviorTreeBuilder Repeat(string name = "Repeat", int times = -1)
    {
        var n = new DecoratorNode(name, (child) => {
            var s = child.Evaluate();
            if (s == BehaviorStatus.Failure) return BehaviorStatus.Failure;
            if (s == BehaviorStatus.Success && --_repeatCount == 0) return BehaviorStatus.Success;
            return BehaviorStatus.Running;
        });
        _repeatCount = times; Attach(n); return this;
    }
    private int _repeatCount = -1;

    public BehaviorTreeBuilder Cooldown(string name, float cooldown)
    {
        float remaining = 0;
        var n = new DecoratorNode(name, (child) => {
            if (remaining > 0) { remaining -= Time.deltaTime; return BehaviorStatus.Failure; }
            var s = child.Evaluate();
            if (s == BehaviorStatus.Success) remaining = cooldown;
            return s;
        });
        Attach(n); return this;
    }

    public BehaviorTreeBuilder AlwaysSucceed(string name = "AlwaysSucceed")
    {
        var n = new DecoratorNode(name, (child) => { child.Evaluate(); return BehaviorStatus.Success; }); Attach(n); return this;
    }

    private void Attach(BehaviorNode n)
    {
        if (_current == null) _tree.Root = n;
        else if (_current is CompositeNode cn) cn.Children.Add(n);
        else if (_current is DecoratorNode dn) dn.Child = n;
        _current = n;
    }

    public void End() { _current = _current?.Parent; }
    public BehaviorTree Build() => _tree;

    private static class Time { public static float deltaTime; public static void SetDelta(float dt) => deltaTime = dt; }
}

public enum BehaviorStatus { Invalid, Success, Failure, Running }

public abstract class BehaviorNode
{
    public string Name; public BehaviorNode? Parent;
    public abstract BehaviorStatus Evaluate();
    public virtual void Reset() { }
}

public sealed class ActionNode : BehaviorNode
{
    private readonly Func<BehaviorStatus> _action;
    public ActionNode(string n, Func<BehaviorStatus> a) { Name = n; _action = a; }
    public override BehaviorStatus Evaluate() => _action();
}

public sealed class ConditionNode : BehaviorNode
{
    private readonly Func<bool> _condition;
    public ConditionNode(string n, Func<bool> c) { Name = n; _condition = c; }
    public override BehaviorStatus Evaluate() => _condition() ? BehaviorStatus.Success : BehaviorStatus.Failure;
}

public abstract class CompositeNode : BehaviorNode
{
    public List<BehaviorNode> Children = new();
    public override void Reset() { foreach (var c in Children) c.Reset(); }
}

public sealed class SelectorNode : CompositeNode
{
    private int _lastRunning;
    public SelectorNode(string n) { Name = n; _lastRunning = -1; }
    public override BehaviorStatus Evaluate()
    {
        int start = _lastRunning >= 0 ? _lastRunning : 0;
        for (int i = start; i < Children.Count; i++)
        {
            var s = Children[i].Evaluate();
            if (s == BehaviorStatus.Running) { _lastRunning = i; return BehaviorStatus.Running; }
            if (s == BehaviorStatus.Success) { _lastRunning = -1; return BehaviorStatus.Success; }
        }
        _lastRunning = -1; return BehaviorStatus.Failure;
    }
    public override void Reset() { _lastRunning = -1; base.Reset(); }
}

public sealed class SequenceNode : CompositeNode
{
    private int _lastRunning;
    public SequenceNode(string n) { Name = n; _lastRunning = -1; }
    public override BehaviorStatus Evaluate()
    {
        int start = _lastRunning >= 0 ? _lastRunning : 0;
        for (int i = start; i < Children.Count; i++)
        {
            var s = Children[i].Evaluate();
            if (s == BehaviorStatus.Running) { _lastRunning = i; return BehaviorStatus.Running; }
            if (s == BehaviorStatus.Failure) { _lastRunning = -1; return BehaviorStatus.Failure; }
        }
        _lastRunning = -1; return BehaviorStatus.Success;
    }
    public override void Reset() { _lastRunning = -1; base.Reset(); }
}

public sealed class ParallelNode : CompositeNode
{
    private readonly int _requiredSuccess;
    public ParallelNode(string n, int required = -1) { Name = n; _requiredSuccess = required; }
    public override BehaviorStatus Evaluate()
    {
        int success = 0, failure = 0;
        foreach (var c in Children)
        {
            var s = c.Evaluate();
            if (s == BehaviorStatus.Success) success++;
            else if (s == BehaviorStatus.Failure) failure++;
        }
        int req = _requiredSuccess >= 0 ? _requiredSuccess : Children.Count;
        if (success >= req) return BehaviorStatus.Success;
        if (failure > Children.Count - req) return BehaviorStatus.Failure;
        return BehaviorStatus.Running;
    }
}

public sealed class DecoratorNode : BehaviorNode
{
    private readonly Func<BehaviorNode, BehaviorStatus> _decorator;
    public BehaviorNode? Child;
    public DecoratorNode(string n, Func<BehaviorNode, BehaviorStatus> d) { Name = n; _decorator = d; }
    public override BehaviorStatus Evaluate() => Child == null ? BehaviorStatus.Failure : _decorator(Child);
}


