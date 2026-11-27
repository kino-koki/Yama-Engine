using System;
using System.Collections.Generic;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>
    /// 脚本绑定门面：把节点句柄、属性状态、信号、生命周期与注册表组合成唯一入口。
    /// 对应 docs/SCRIPTING_ABI_SPEC.md 的 L2 绑定 ABI 层。
    /// </summary>
    public sealed class ScriptBinding
    {
        private readonly NodeHandleTable _handles;
        private readonly ScriptRegistry _registry;

        public string TypeName { get; }
        public NodeRef Node { get; }
        public PropertyBag Properties { get; }
        public SignalBus Signals { get; }
        public LifecycleDispatcher Lifecycle { get; }

        public ScriptBinding(string typeName, NodeRef node, NodeHandleTable handles, ScriptRegistry registry)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Node = node;
            _handles = handles ?? throw new ArgumentNullException(nameof(handles));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Properties = new PropertyBag();
            Signals = new SignalBus();
            Lifecycle = new LifecycleDispatcher();
        }

        public bool TryResolveNode(out object? target) => _handles.TryResolve(Node, out target);

        public object Instantiate() => _registry.Create(TypeName, this);

        /// <summary>热重载前捕获状态。</summary>
        public PropertyBag CaptureState() => Properties.Clone();

        /// <summary>热重载后迁移状态：只补新实例里缺失的键（硬约束 3）。</summary>
        public int RestoreState(PropertyBag previous) => PropertyBag.Migrate(previous, Properties);

        public IReadOnlyDictionary<string, ScriptVariant> DescribeState()
        {
            var snapshot = Properties.Snapshot();
            return new Dictionary<string, ScriptVariant>(snapshot, StringComparer.Ordinal);
        }
    }
}
