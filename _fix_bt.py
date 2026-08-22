# 修复 AI 行为树类层次：恢复 BTV2 独立基类，V1 任务类接 BTTaskV1
import os, re

base = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ShanyuEngine', 'AI')

# ---------- 1. BehaviorTreeV2.cs ----------
p = os.path.join(base, 'BehaviorTreeV2.cs')
text = open(p, encoding='utf-8').read()

# 1a. 插入 BTNodeV2 / BTDecoratorV2 基类（在 BTSequence 之前）
base_classes = '''
    // ========================================================================
    //  二点五、V2 节点基类（去重修复时恢复：V2 节点需要独立的执行上下文签名）
    // ========================================================================

    /// <summary>V2 行为树节点基类。与 V1 的 BTNode 隔离，使用 BTContext/BTNodeStatus。</summary>
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
    }

'''
anchor = '    public sealed class BTSequence : BTNode'
assert anchor in text, 'anchor not found'
text = text.replace(anchor, base_classes + anchor.replace(': BTNode', ': BTNodeV2'), 1)

# 1b. 全文替换基类引用（\b 保护 BTNodeStatus 等）
text = re.sub(r'\bBTNode\b(?!Status)', 'BTNodeV2', text)
text = re.sub(r'\bBTDecorator\b(?!V2)', 'BTDecoratorV2', text)

# 1c. BTContext 字段/方法同名冲突：方法改名 Warn/Error
text = text.replace(
    '        public void LogWarning(string msg) => LogWarning?.Invoke(msg);\n        public void LogError(string msg) => LogError?.Invoke(msg);',
    '        public void Warn(string msg) => LogWarning?.Invoke(msg);\n        public void Error(string msg) => LogError?.Invoke(msg);')
text = text.replace('context.LogError($', 'context.Error($')
open(p, 'w', encoding='utf-8', newline='').write(text)
print('BehaviorTreeV2.cs done')

# ---------- 2. BehaviorTreeSystem.cs ----------
p = os.path.join(base, 'BehaviorTreeSystem.cs')
text = open(p, encoding='utf-8').read()
# 2a. 插入 BTTaskV1 基类（在第一个 BTTask_ 之前）
v1_base = '''
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

'''
anchor = '    public sealed class BTTask_MoveTo : BTTask'
assert anchor in text, 'v1 anchor not found'
text = text.replace(anchor, v1_base + anchor.replace(': BTTask', ': BTTaskV1'), 1)
# 2b. 其余任务类改继承
text = re.sub(r': BTTask\r?$', ': BTTaskV1', text, flags=re.M)
# 2c. InitNode 可访问性与基类对齐
text = text.replace('protected override void InitNode(BTNodeContext ctx)', 'public override void InitNode(BTNodeContext ctx)')
open(p, 'w', encoding='utf-8', newline='').write(text)
print('BehaviorTreeSystem.cs done')

# ---------- 3. LLMIntelligenceSystem.cs ----------
p = os.path.join(base, 'LLMIntelligenceSystem.cs')
text = open(p, encoding='utf-8').read()
text = re.sub(r'\bBTNode\b(?!Status)', 'BTNodeV2', text)
open(p, 'w', encoding='utf-8', newline='').write(text)
print('LLMIntelligenceSystem.cs done')

# ---------- 4. SmartObjectsSystem.cs ----------
p = os.path.join(base, 'SmartObjectsSystem.cs')
text = open(p, encoding='utf-8').read()
text = text.replace('public sealed class BTTask_UseSmartObject : BTNode',
                    'public sealed class BTTask_UseSmartObject : BTNodeV2')
open(p, 'w', encoding='utf-8', newline='').write(text)
print('SmartObjectsSystem.cs done')
