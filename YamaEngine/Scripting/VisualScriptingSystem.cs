using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using YamaEngine.Core;
using YamaEngine.ECS;
using YamaEngine.Gameplay;

namespace YamaEngine.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //  yama引擎 · 可视化脚本系统（蓝图系统）  Visual Scripting / NodeScript System
    //  ────────────────────────────────────────────────────────────────────────────
    //  本文件实现一套完整的节点式可视化脚本系统，对齐虚幻引擎 NodeScript 的核心模型：
    //    · 节点（Node）—— 引脚（Pin）—— 连线（Link）—— 图（Graph）
    //    · 事件驱动 + 数据流拉取（pull-based data flow）+ 执行流推动（exec-pin push）
    //    · 与 ECS（EntityComponentSystem / IComponent / ISystem）深度对接
    //    · 与 ScriptingSystem（Roslyn 动态编译 ScriptEngine）对接，支持蓝图编译为 C#
    //    · 与 Gameplay（ActorComponent / Actor / GameplayWorld）对接
    //  作者：yama引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  第一部分：基础枚举与类型工具
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图引脚支持的数据类型。Exec 表示执行流引脚（白色脉冲引脚），
    /// 其余为数据引脚。该枚举同时用于类型检查与序列化。
    /// </summary>
    public enum NodeScriptDataType
    {
        /// <summary>执行流引脚，驱动节点顺序执行。</summary>
        Exec,
        /// <summary>布尔值。</summary>
        Bool,
        /// <summary>32 位整数。</summary>
        Int,
        /// <summary>单精度浮点数。</summary>
        Float,
        /// <summary>字符串。</summary>
        String,
        /// <summary>三维向量（System.Numerics.Vector3）。</summary>
        Vector3,
        /// <summary>四元数旋转。</summary>
        Quaternion,
        /// <summary>ECS 实体 ID（int）。</summary>
        Entity,
        /// <summary>任意对象引用（弱类型）。</summary>
        Object
    }

    /// <summary>引脚方向：输入或输出。</summary>
    public enum PinDirection
    {
        /// <summary>输入引脚（左侧）。</summary>
        Input,
        /// <summary>输出引脚（右侧）。</summary>
        Output
    }

    /// <summary>蓝图节点分类，用于编辑器分组与颜色标识。</summary>
    public enum NodeType
    {
        /// <summary>事件节点：蓝图执行入口（红色）。</summary>
        Event,
        /// <summary>函数节点：调用方法或读写数据（蓝色）。</summary>
        Function,
        /// <summary>流程控制节点：分支/循环/门控（灰色）。</summary>
        FlowControl,
        /// <summary>变量节点：读写局部/实例变量（青色）。</summary>
        Variable,
        /// <summary>运算符节点：数学/逻辑运算（绿色）。</summary>
        Operator,
        /// <summary>注释节点：纯说明，不参与执行。</summary>
        Comment
    }

    /// <summary>
    /// 蓝图数据类型工具集：提供默认值、CLR 类型映射与类型转换，
    /// 供执行器与编译器共享。
    /// </summary>
    public static class NodeScriptTypeUtils
    {
        /// <summary>返回指定蓝图类型的默认值（盒装）。</summary>
        public static object? GetDefault(NodeScriptDataType type) => type switch
        {
            NodeScriptDataType.Exec => null,
            NodeScriptDataType.Bool => false,
            NodeScriptDataType.Int => 0,
            NodeScriptDataType.Float => 0f,
            NodeScriptDataType.String => string.Empty,
            NodeScriptDataType.Vector3 => Vector3.Zero,
            NodeScriptDataType.Quaternion => Quaternion.Identity,
            NodeScriptDataType.Entity => 0,
            NodeScriptDataType.Object => null,
            _ => null
        };

        /// <summary>返回蓝图类型对应的 CLR 类型。</summary>
        public static Type GetClrType(NodeScriptDataType type) => type switch
        {
            NodeScriptDataType.Exec => typeof(void),
            NodeScriptDataType.Bool => typeof(bool),
            NodeScriptDataType.Int => typeof(int),
            NodeScriptDataType.Float => typeof(float),
            NodeScriptDataType.String => typeof(string),
            NodeScriptDataType.Vector3 => typeof(Vector3),
            NodeScriptDataType.Quaternion => typeof(Quaternion),
            NodeScriptDataType.Entity => typeof(int),
            NodeScriptDataType.Object => typeof(object),
            _ => typeof(object)
        };

        /// <summary>
        /// 尝试将任意值转换为目标蓝图类型。用于数据引脚跨类型连接时的
        /// 宽松匹配（如 Int→Float、Float→Int、String→数字）。
        /// </summary>
        public static bool TryConvert(object? value, NodeScriptDataType target, out object? result)
        {
            result = GetDefault(target);
            if (value == null)
            {
                return target == NodeScriptDataType.Object || target == NodeScriptDataType.String;
            }

            try
            {
                result = target switch
                {
                    NodeScriptDataType.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                    NodeScriptDataType.Int => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    NodeScriptDataType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                    NodeScriptDataType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
                    NodeScriptDataType.Vector3 => value is Vector3 v ? v : Vector3.Zero,
                    NodeScriptDataType.Quaternion => value is Quaternion q ? q : Quaternion.Identity,
                    NodeScriptDataType.Entity => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    NodeScriptDataType.Object => value,
                    _ => value
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>判断两个蓝图类型是否可相互连接（含宽松数值转换）。</summary>
        public static bool AreCompatible(NodeScriptDataType a, NodeScriptDataType b)
        {
            if (a == b) return true;
            // 数值族互通
            var numeric = new HashSet<NodeScriptDataType>
            {
                NodeScriptDataType.Int, NodeScriptDataType.Float, NodeScriptDataType.Entity
            };
            if (numeric.Contains(a) && numeric.Contains(b)) return true;
            // Object 万能兜底
            if (a == NodeScriptDataType.Object || b == NodeScriptDataType.Object) return true;
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第二部分：引脚、连线、节点与图
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图引脚：节点上的连接点。分输入/输出，分执行(Exec)/数据。
    /// 数据引脚可持有字面量默认值（未连接时使用）与运行时缓存值。
    /// </summary>
    public sealed class NodeScriptPin
    {
        /// <summary>引脚唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>引脚显示名称（如 "Then"、"True"、"A"）。</summary>
        public string Name { get; set; }

        /// <summary>引脚方向。</summary>
        public PinDirection Direction { get; set; }

        /// <summary>引脚数据类型。</summary>
        public NodeScriptDataType DataType { get; set; }

        /// <summary>未连接时使用的字面量默认值（输入数据引脚常用）。</summary>
        public object? DefaultValue { get; set; }

        /// <summary>运行时缓存值：执行型节点在 Execute 后将输出数据写入此处，供数据拉取读取。</summary>
        public object? CachedValue { get; set; }

        /// <summary>编辑器中的相对偏移（仅 UI 用途）。</summary>
        public Vector2 EditorOffset { get; set; }

        /// <summary>是否为执行流引脚。</summary>
        public bool IsExec => DataType == NodeScriptDataType.Exec;

        /// <summary>构造引脚。</summary>
        public NodeScriptPin(string name, PinDirection direction, NodeScriptDataType dataType, object? defaultValue = null)
        {
            Name = name ?? string.Empty;
            Direction = direction;
            DataType = dataType;
            DefaultValue = defaultValue;
            CachedValue = null;
        }

        /// <summary>重置运行时缓存（每次事件触发前调用）。</summary>
        public void ResetCache() => CachedValue = null;
    }

    /// <summary>
    /// 蓝图连线：从源输出引脚到目标输入引脚的有向连接。
    /// 支持多对多（一个输出可连多个输入，一个输入仅接受一条来源——
    /// 后者由编辑器约束，运行时取第一条）。
    /// </summary>
    public sealed class NodeScriptLink
    {
        /// <summary>连线唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>源节点 ID。</summary>
        public Guid SourceNodeId { get; set; }

        /// <summary>源输出引脚 ID。</summary>
        public Guid SourcePinId { get; set; }

        /// <summary>目标节点 ID。</summary>
        public Guid TargetNodeId { get; set; }

        /// <summary>目标输入引脚 ID。</summary>
        public Guid TargetPinId { get; set; }

        /// <summary>构造连线。</summary>
        public NodeScriptLink(Guid sourceNodeId, Guid sourcePinId, Guid targetNodeId, Guid targetPinId)
        {
            SourceNodeId = sourceNodeId;
            SourcePinId = sourcePinId;
            TargetNodeId = targetNodeId;
            TargetPinId = targetPinId;
        }
    }

    /// <summary>
    /// 蓝图节点基类。所有节点拥有输入/输出引脚列表，并提供两个核心虚方法：
    ///   · <see cref="Execute"/> —— 执行流到达本节点时调用，返回下一个要跟随的输出执行引脚。
    ///   · <see cref="EvaluateOutput"/> —— 数据拉取时调用，返回指定输出数据引脚的值。
    /// 纯数据节点（IsPure=true）通常不重写 Execute（默认返回 null，不推动执行流），
    /// 而是重写 EvaluateOutput 即时计算。执行型节点在 Execute 中完成副作用并把结果写入
    /// 输出数据引脚的 CachedValue，EvaluateOutput 默认返回该缓存。
    /// </summary>
    public abstract class NodeScriptNode
    {
        /// <summary>节点唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>节点显示标题。</summary>
        public string Title { get; set; }

        /// <summary>节点分类。</summary>
        public NodeType NodeType { get; protected set; } = NodeType.Function;

        /// <summary>输入引脚列表。</summary>
        public List<NodeScriptPin> InputPins { get; } = new();

        /// <summary>输出引脚列表。</summary>
        public List<NodeScriptPin> OutputPins { get; } = new();

        /// <summary>编辑器坐标（仅 UI 用途）。</summary>
        public Vector2 EditorPosition { get; set; }

        /// <summary>是否为纯数据节点（无执行流引脚，由数据拉取驱动）。</summary>
        public virtual bool IsPure => false;

        /// <summary>节点工厂名称（用于序列化反序列化与编译器识别）。</summary>
        public virtual string FactoryName => GetType().Name;

        protected NodeScriptNode(string title)
        {
            Title = title ?? string.Empty;
        }

        // ── 引脚构造辅助 ────────────────────────────────────────────────────────

        /// <summary>添加一个输入执行引脚（名为 "Exec"）。</summary>
        protected NodeScriptPin AddExecInput() =>
            AddPin("Exec", PinDirection.Input, NodeScriptDataType.Exec);

        /// <summary>添加一个输出执行引脚。</summary>
        protected NodeScriptPin AddExecOutput(string name = "Then") =>
            AddPin(name, PinDirection.Output, NodeScriptDataType.Exec);

        /// <summary>添加一个输入数据引脚。</summary>
        protected NodeScriptPin AddDataInput(string name, NodeScriptDataType type, object? defaultValue = null) =>
            AddPin(name, PinDirection.Input, type, defaultValue);

        /// <summary>添加一个输出数据引脚。</summary>
        protected NodeScriptPin AddDataOutput(string name, NodeScriptDataType type) =>
            AddPin(name, PinDirection.Output, type);

        protected NodeScriptPin AddPin(string name, PinDirection dir, NodeScriptDataType type, object? defaultValue = null)
        {
            var pin = new NodeScriptPin(name, dir, type, defaultValue);
            (dir == PinDirection.Input ? InputPins : OutputPins).Add(pin);
            return pin;
        }

        // ── 引脚查询辅助 ────────────────────────────────────────────────────────

        /// <summary>获取（唯一的）输入执行引脚，没有则返回 null。</summary>
        public NodeScriptPin? GetInputExecPin() => InputPins.FirstOrDefault(p => p.IsExec);

        /// <summary>按名获取输出执行引脚。</summary>
        public NodeScriptPin? GetOutputExecPin(string name) =>
            OutputPins.FirstOrDefault(p => p.IsExec && p.Name == name);

        /// <summary>所有输出执行引脚。</summary>
        public IEnumerable<NodeScriptPin> OutputExecPins => OutputPins.Where(p => p.IsExec);

        /// <summary>按名获取输入数据引脚。</summary>
        public NodeScriptPin? GetInputPin(string name) => InputPins.FirstOrDefault(p => p.Name == name);

        /// <summary>按名获取输出数据引脚。</summary>
        public NodeScriptPin? GetOutputPin(string name) => OutputPins.FirstOrDefault(p => p.Name == name);

        // ── 执行与求值（核心虚方法） ────────────────────────────────────────────

        /// <summary>
        /// 执行流到达本节点时调用。执行副作用后返回应跟随的下一个输出执行引脚；
        /// 返回 null 表示当前执行链终止。纯节点默认返回 null。
        /// </summary>
        public virtual NodeScriptPin? Execute(NodeScriptExecutionContext ctx) => null;

        /// <summary>
        /// 数据拉取时调用，返回指定输出数据引脚的值。执行型节点默认返回
        /// <see cref="NodeScriptPin.CachedValue"/>（由 Execute 写入）。
        /// 纯节点重写此方法即时计算。
        /// </summary>
        public virtual object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx) =>
            pin.CachedValue ?? pin.DefaultValue;

        /// <summary>从输入数据引脚拉取值（经执行器解析连线与递归求值）。</summary>
        protected object? EvaluateInput(string pinName, NodeScriptExecutionContext ctx)
        {
            var pin = GetInputPin(pinName);
            if (pin == null) return null;
            return ctx.Executor.EvaluateDataPin(pin, ctx);
        }

        /// <summary>从输入数据引脚拉取值并转换为指定类型。</summary>
        protected T? EvaluateInputAs<T>(string pinName, NodeScriptExecutionContext ctx)
        {
            var val = EvaluateInput(pinName, ctx);
            if (val is T t) return t;
            if (val == null) return default;
            try { return (T)Convert.ChangeType(val, typeof(T), CultureInfo.InvariantCulture); }
            catch { return default; }
        }

        /// <summary>重置所有输出引脚缓存（事件触发前调用，避免脏读）。</summary>
        public virtual void ResetCaches()
        {
            foreach (var p in OutputPins) p.ResetCache();
        }
    }

    /// <summary>
    /// 蓝图图：一组节点与连线的集合，包含事件入口点。
    /// 这是蓝图的逻辑结构容器，可序列化为 <see cref="NodeScriptAsset"/>。
    /// </summary>
    public sealed class NodeScriptGraph
    {
        /// <summary>图中所有节点。</summary>
        public List<NodeScriptNode> Nodes { get; } = new();

        /// <summary>图中所有连线。</summary>
        public List<NodeScriptLink> Links { get; } = new();

        /// <summary>事件入口节点 ID 列表（事件节点）。</summary>
        public List<Guid> EntryPointNodeIds { get; } = new();

        /// <summary>添加节点并返回该节点。</summary>
        public T AddNode<T>(T node) where T : NodeScriptNode
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            Nodes.Add(node);
            if (node is EventNode ev && !EntryPointNodeIds.Contains(node.Id))
                EntryPointNodeIds.Add(node.Id);
            return node;
        }

        /// <summary>按 ID 查找节点。</summary>
        public NodeScriptNode? FindNode(Guid id) => Nodes.FirstOrDefault(n => n.Id == id);

        /// <summary>按引脚 ID 查找所属节点。</summary>
        public NodeScriptNode? FindNodeByPin(Guid pinId) =>
            Nodes.FirstOrDefault(n => n.InputPins.Any(p => p.Id == pinId) || n.OutputPins.Any(p => p.Id == pinId));

        /// <summary>按 ID 查找引脚。</summary>
        public NodeScriptPin? FindPin(Guid pinId)
        {
            foreach (var n in Nodes)
            {
                var p = n.InputPins.FirstOrDefault(x => x.Id == pinId);
                if (p != null) return p;
                p = n.OutputPins.FirstOrDefault(x => x.Id == pinId);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>创建一条连线（自动校验引脚方向与类型兼容性）。</summary>
        public NodeScriptLink? Link(Guid sourcePinId, Guid targetPinId)
        {
            var src = FindPin(sourcePinId);
            var tgt = FindPin(targetPinId);
            if (src == null || tgt == null) return null;
            if (src.Direction != PinDirection.Output || tgt.Direction != PinDirection.Input) return null;
            if (!NodeScriptTypeUtils.AreCompatible(src.DataType, tgt.DataType)) return null;

            // 数据输入引脚只允许一条来源：移除旧连线
            if (!src.IsExec)
            {
                Links.RemoveAll(l => l.TargetPinId == targetPinId);
            }

            var srcNode = FindNodeByPin(sourcePinId)!;
            var tgtNode = FindNodeByPin(targetPinId)!;
            var link = new NodeScriptLink(srcNode.Id, sourcePinId, tgtNode.Id, targetPinId);
            Links.Add(link);
            return link;
        }

        /// <summary>移除一条连线。</summary>
        public bool RemoveLink(Guid linkId) => Links.RemoveAll(l => l.Id == linkId) > 0;

        /// <summary>查找从指定输出引脚出发的连线。</summary>
        public IEnumerable<NodeScriptLink> LinksFrom(Guid sourcePinId) =>
            Links.Where(l => l.SourcePinId == sourcePinId);

        /// <summary>查找连入指定输入引脚的连线（数据输入仅一条，执行可多条）。</summary>
        public NodeScriptLink? LinkTo(Guid targetPinId) =>
            Links.FirstOrDefault(l => l.TargetPinId == targetPinId);

        /// <summary>获取从指定输出执行引脚连到的目标输入引脚（取第一条）。</summary>
        public NodeScriptPin? GetLinkedTargetPin(NodeScriptPin sourceOutputPin)
        {
            var link = Links.FirstOrDefault(l => l.SourcePinId == sourceOutputPin.Id);
            return link == null ? null : FindPin(link.TargetPinId);
        }

        /// <summary>获取连入指定输入引脚的源输出引脚。</summary>
        public NodeScriptPin? GetLinkedSourcePin(NodeScriptPin targetInputPin)
        {
            var link = Links.FirstOrDefault(l => l.TargetPinId == targetInputPin.Id);
            return link == null ? null : FindPin(link.SourcePinId);
        }

        /// <summary>获取所有事件节点。</summary>
        public IEnumerable<EventNode> EventNodes => Nodes.OfType<EventNode>();

        /// <summary>按事件名查找事件节点。</summary>
        public EventNode? FindEvent(string eventName) =>
            Nodes.OfType<EventNode>().FirstOrDefault(n => n.EventName == eventName);

        /// <summary>重置全图节点缓存。</summary>
        public void ResetAllCaches()
        {
            foreach (var n in Nodes) n.ResetCaches();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第三部分：事件节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 事件节点基类：蓝图的执行入口。每个事件节点有一个输出执行引脚 "Then"，
    /// 以及若干输出数据引脚表示事件参数（从 <see cref="NodeScriptExecutionContext.EventArguments"/> 读取）。
    /// 事件节点本身不参与数据拉取的副作用，仅在 EvaluateOutput 时返回事件参数。
    /// </summary>
    public abstract class EventNode : NodeScriptNode
    {
        /// <summary>事件名称（如 "OnBeginPlay"），用于触发匹配。</summary>
        public abstract string EventName { get; }

        /// <summary>事件描述（编辑器显示）。</summary>
        public virtual string Description => EventName;

        protected EventNode(string title) : base(title)
        {
            NodeType = NodeType.Event;
            // 事件节点默认有一个 "Then" 输出执行引脚
            AddExecOutput("Then");
        }

        /// <summary>事件参数从上下文 EventArguments 读取。</summary>
        public override object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx)
        {
            if (ctx.EventArguments != null && ctx.EventArguments.TryGetValue(pin.Name, out var v))
                return v;
            return pin.DefaultValue;
        }
    }

    // ── 生命周期事件 ──────────────────────────────────────────────────────────────

    /// <summary>游戏开始时触发（对应 Actor/Script 的 BeginPlay）。</summary>
    public sealed class OnBeginPlayNode : EventNode
    {
        public override string EventName => "OnBeginPlay";
        public OnBeginPlayNode() : base("On Begin Play") { }
    }

    /// <summary>游戏结束/销毁时触发。</summary>
    public sealed class OnEndPlayNode : EventNode
    {
        public override string EventName => "OnEndPlay";
        public OnEndPlayNode() : base("On End Play") { }
    }

    /// <summary>每帧触发，输出 DeltaSeconds 数据引脚。</summary>
    public sealed class OnTickNode : EventNode
    {
        public override string EventName => "OnTick";
        public OnTickNode() : base("Event Tick")
        {
            AddDataOutput("Delta Seconds", NodeScriptDataType.Float);
        }
    }

    /// <summary>物理碰撞时触发，输出 Other Entity。</summary>
    public sealed class OnCollisionNode : EventNode
    {
        public override string EventName => "OnCollision";
        public OnCollisionNode() : base("On Collision")
        {
            AddDataOutput("Other Entity", NodeScriptDataType.Entity);
            AddDataOutput("Hit Point", NodeScriptDataType.Vector3);
            AddDataOutput("Impact Normal", NodeScriptDataType.Vector3);
        }
    }

    /// <summary>重叠区域进入时触发。</summary>
    public sealed class OnOverlapNode : EventNode
    {
        public override string EventName => "OnOverlap";
        public OnOverlapNode() : base("On Overlap")
        {
            AddDataOutput("Other Entity", NodeScriptDataType.Entity);
        }
    }

    /// <summary>自定义事件节点：通过名称触发，可携带任意参数。</summary>
    public sealed class OnCustomEventNode : EventNode
    {
        public override string EventName => CustomName;
        /// <summary>自定义事件名（可编辑）。</summary>
        public string CustomName { get; set; } = "CustomEvent";
        public OnCustomEventNode() : base("Custom Event") { }
    }

    // ── 输入事件 ──────────────────────────────────────────────────────────────────

    /// <summary>按键按下时触发，输出 Key 名称。</summary>
    public sealed class OnKeyPressedNode : EventNode
    {
        public override string EventName => "OnKeyPressed";
        /// <summary>监听的按键名（默认空表示任意键）。</summary>
        public string KeyName { get; set; } = string.Empty;
        public OnKeyPressedNode() : base("Key Pressed")
        {
            AddDataOutput("Key", NodeScriptDataType.String);
        }
    }

    /// <summary>按键松开时触发。</summary>
    public sealed class OnKeyReleasedNode : EventNode
    {
        public override string EventName => "OnKeyReleased";
        public string KeyName { get; set; } = string.Empty;
        public OnKeyReleasedNode() : base("Key Released")
        {
            AddDataOutput("Key", NodeScriptDataType.String);
        }
    }

    /// <summary>输入轴持续输入时触发，输出轴值。</summary>
    public sealed class OnAxisInputNode : EventNode
    {
        public override string EventName => "OnAxisInput";
        /// <summary>轴名称（如 "MoveForward"）。</summary>
        public string AxisName { get; set; } = "MoveForward";
        public OnAxisInputNode() : base("Axis Input")
        {
            AddDataOutput("Axis Value", NodeScriptDataType.Float);
        }
    }

    // ── 游戏事件 ──────────────────────────────────────────────────────────────────

    /// <summary>受到伤害时触发，输出 Damage 量与伤害来源实体。</summary>
    public sealed class OnTakeDamageNode : EventNode
    {
        public override string EventName => "OnTakeDamage";
        public OnTakeDamageNode() : base("On Take Damage")
        {
            AddDataOutput("Damage", NodeScriptDataType.Float);
            AddDataOutput("Instigator", NodeScriptDataType.Entity);
        }
    }

    /// <summary>死亡时触发。</summary>
    public sealed class OnDeathNode : EventNode
    {
        public override string EventName => "OnDeath";
        public OnDeathNode() : base("On Death")
        {
            AddDataOutput("Killer", NodeScriptDataType.Entity);
        }
    }

    /// <summary>生成时触发。</summary>
    public sealed class OnSpawnNode : EventNode
    {
        public override string EventName => "OnSpawn";
        public OnSpawnNode() : base("On Spawn") { }
    }

    /// <summary>交互时触发（玩家与对象交互）。</summary>
    public sealed class OnInteractNode : EventNode
    {
        public override string EventName => "OnInteract";
        public OnInteractNode() : base("On Interact")
        {
            AddDataOutput("Instigator", NodeScriptDataType.Entity);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第四部分：流程控制节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 分支节点：根据布尔条件选择执行 True 或 False 分支。
    /// 两个分支各自作为独立子链执行（ExecuteChain），执行后当前链终止。
    /// </summary>
    public sealed class BranchNode : NodeScriptNode
    {
        public BranchNode() : base("Branch")
        {
            NodeType = NodeType.FlowControl;
            AddDataInput("Condition", NodeScriptDataType.Bool, false);
            AddExecOutput("True");
            AddExecOutput("False");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var cond = EvaluateInputAs<bool>("Condition", ctx);
            var chosen = cond ? GetOutputExecPin("True") : GetOutputExecPin("False");
            // 将选中分支作为子链完整执行，然后终止当前链
            ctx.Executor.ExecuteChain(chosen, ctx);
            return null;
        }
    }

    /// <summary>
    /// 顺序节点：依次执行多个输出执行引脚（Then 0..N），各为独立子链。
    /// 执行完所有子链后终止。
    /// </summary>
    public sealed class SequenceNode : NodeScriptNode
    {
        public int OutputCount { get; }

        public SequenceNode(int outputCount = 4) : base("Sequence")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            OutputCount = Math.Max(2, outputCount);
            for (int i = 0; i < OutputCount; i++)
                AddExecOutput($"Then {i}");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            // 依次执行每个输出子链
            foreach (var pin in OutputExecPins)
            {
                ctx.Executor.ExecuteChain(pin, ctx);
            }
            return null;
        }
    }

    /// <summary>
    /// While 循环节点：当 Condition 为真时反复执行 Loop Body 子链，
    /// 结束后从 Completed 继续。含最大迭代数保护防止死循环。
    /// </summary>
    public sealed class WhileLoopNode : NodeScriptNode
    {
        public WhileLoopNode() : base("While Loop")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("Condition", NodeScriptDataType.Bool, false);
            AddExecOutput("Loop Body");
            AddExecOutput("Completed");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            int iter = 0;
            var body = GetOutputExecPin("Loop Body");
            while (EvaluateInputAs<bool>("Condition", ctx))
            {
                if (iter++ >= NodeScriptExecutor.MaxLoopIterations)
                {
                    ctx.Executor.ReportError(ctx, this, "WhileLoop 超过最大迭代限制，已中断以防死循环。");
                    break;
                }
                ctx.Executor.ExecuteChain(body, ctx);
            }
            return GetOutputExecPin("Completed");
        }
    }

    /// <summary>
    /// For 循环节点：从 First 索引到 Last 索引（含），每次执行 Loop Body 并输出当前索引。
    /// </summary>
    public sealed class ForLoopNode : NodeScriptNode
    {
        public ForLoopNode() : base("For Loop")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("First Index", NodeScriptDataType.Int, 0);
            AddDataInput("Last Index", NodeScriptDataType.Int, 9);
            AddExecOutput("Loop Body");
            AddDataOutput("Index", NodeScriptDataType.Int);
            AddExecOutput("Completed");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var first = EvaluateInputAs<int>("First Index", ctx);
            var last = EvaluateInputAs<int>("Last Index", ctx);
            var body = GetOutputExecPin("Loop Body");
            var indexOut = GetOutputPin("Index");

            int iter = 0;
            for (int i = first; i <= last; i++)
            {
                if (iter++ >= NodeScriptExecutor.MaxLoopIterations)
                {
                    ctx.Executor.ReportError(ctx, this, "ForLoop 超过最大迭代限制，已中断。");
                    break;
                }
                if (indexOut != null) indexOut.CachedValue = i;
                ctx.Executor.ExecuteChain(body, ctx);
            }
            return GetOutputExecPin("Completed");
        }
    }

    /// <summary>
    /// ForEach 遍历节点：遍历数组（Object 类型，需为 IEnumerable），
    /// 每次输出当前元素与索引，执行 Loop Body，结束后从 Completed 继续。
    /// </summary>
    public sealed class ForEachLoopNode : NodeScriptNode
    {
        public ForEachLoopNode() : base("For Each Loop")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("Array", NodeScriptDataType.Object);
            AddExecOutput("Loop Body");
            AddDataOutput("Array Element", NodeScriptDataType.Object);
            AddDataOutput("Index", NodeScriptDataType.Int);
            AddExecOutput("Completed");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var array = EvaluateInput("Array", ctx);
            var elemOut = GetOutputPin("Array Element");
            var indexOut = GetOutputPin("Index");
            var body = GetOutputExecPin("Loop Body");

            if (array is System.Collections.IEnumerable enumerable)
            {
                int i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= NodeScriptExecutor.MaxLoopIterations)
                    {
                        ctx.Executor.ReportError(ctx, this, "ForEachLoop 超过最大迭代限制，已中断。");
                        break;
                    }
                    if (elemOut != null) elemOut.CachedValue = item;
                    if (indexOut != null) indexOut.CachedValue = i;
                    ctx.Executor.ExecuteChain(body, ctx);
                    i++;
                }
            }
            return GetOutputExecPin("Completed");
        }
    }

    /// <summary>
    /// 门控节点：Open/Close/Toggle 控制开关，Enter 到来时若门开则从 Exit 输出，否则阻断。
    /// 状态保存在 <see cref="NodeScriptInstance.State"/>。
    /// </summary>
    public sealed class GateNode : NodeScriptNode
    {
        public GateNode() : base("Gate")
        {
            NodeType = NodeType.FlowControl;
            // 多个执行输入：Enter / Open / Close / Toggle
            AddPin("Enter", PinDirection.Input, NodeScriptDataType.Exec);
            AddPin("Open", PinDirection.Input, NodeScriptDataType.Exec);
            AddPin("Close", PinDirection.Input, NodeScriptDataType.Exec);
            AddPin("Toggle", PinDirection.Input, NodeScriptDataType.Exec);
            AddExecOutput("Exit");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            // 判断是从哪个输入引脚进来的——通过检查调用栈顶对应的输入引脚连线
            // 简化实现：用实例状态记录门开关，Enter 时判定
            // 由于本执行器按引脚驱动，这里通过上下文记录的"入口输入引脚"判断
            var enteredVia = ctx.CurrentInputPin;
            bool gateOpen = ctx.Instance.GetState<bool>($"{Id}::GateOpen", true);

            if (enteredVia != null && enteredVia.Name == "Open") { ctx.Instance.SetState($"{Id}::GateOpen", true); return null; }
            if (enteredVia != null && enteredVia.Name == "Close") { ctx.Instance.SetState($"{Id}::GateOpen", false); return null; }
            if (enteredVia != null && enteredVia.Name == "Toggle") { ctx.Instance.SetState($"{Id}::GateOpen", !gateOpen); return null; }

            // Enter
            if (gateOpen) return GetOutputExecPin("Exit");
            return null;
        }
    }

    /// <summary>
    /// 延迟节点：将后续执行流挂起 DelaySeconds 秒后由执行器的 Update 恢复。
    /// 在顶层事件链中工作正确；循环体内的延迟为尽力而为（不阻塞循环）。
    /// </summary>
    public sealed class DelayNode : NodeScriptNode
    {
        public DelayNode() : base("Delay")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("Duration", NodeScriptDataType.Float, 1f);
            AddExecOutput("Completed");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var duration = EvaluateInputAs<float>("Duration", ctx);
            var next = GetOutputExecPin("Completed");
            if (next == null) return null;
            // 调度延迟续接：当前执行链终止，到时间后从 Completed 续接
            ctx.Executor.ScheduleContinuation(ctx, next.Id, duration);
            return null;
        }
    }

    /// <summary>
    /// 只执行一次节点：首次执行时从 Then 输出并锁定；后续执行被阻断。
    /// 提供 Reset 输入引脚重置标志。
    /// </summary>
    public sealed class DoOnceNode : NodeScriptNode
    {
        public DoOnceNode() : base("Do Once")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddExecOutput("Completed");
            // Reset 作为单独的执行输入
            AddPin("Reset", PinDirection.Input, NodeScriptDataType.Exec);
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var enteredVia = ctx.CurrentInputPin;
            if (enteredVia != null && enteredVia.Name == "Reset")
            {
                ctx.Instance.SetState($"{Id}::Done", false);
                return null;
            }
            bool done = ctx.Instance.GetState<bool>($"{Id}::Done", false);
            if (done) return null;
            ctx.Instance.SetState($"{Id}::Done", true);
            return GetOutputExecPin("Completed");
        }
    }

    /// <summary>
    /// 整数 Switch：根据 Selection 值选择对应 case 输出执行；无匹配则走 Default。
    /// </summary>
    public sealed class SwitchOnIntNode : NodeScriptNode
    {
        /// <summary>case 值列表（可编辑）。</summary>
        public List<int> CaseValues { get; } = new();

        public SwitchOnIntNode() : base("Switch on Int")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("Selection", NodeScriptDataType.Int, 0);
            AddExecOutput("Default");
        }

        /// <summary>添加一个 case 并返回其执行输出引脚。</summary>
        public NodeScriptPin AddCase(int value)
        {
            CaseValues.Add(value);
            var pin = AddExecOutput($"Case {value}");
            return pin;
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var sel = EvaluateInputAs<int>("Selection", ctx);
            // 找到对应 case 输出引脚（按添加顺序匹配 CaseValues）
            int idx = CaseValues.IndexOf(sel);
            if (idx >= 0)
            {
                // case 引脚排在 Default 之后
                var casePins = OutputExecPins.Where(p => p.Name != "Default").ToList();
                if (idx < casePins.Count)
                {
                    ctx.Executor.ExecuteChain(casePins[idx], ctx);
                    return null;
                }
            }
            // Default
            ctx.Executor.ExecuteChain(GetOutputExecPin("Default"), ctx);
            return null;
        }
    }

    /// <summary>字符串 Switch：根据 Selection 字符串选择 case。</summary>
    public sealed class SwitchOnStringNode : NodeScriptNode
    {
        public List<string> CaseValues { get; } = new();

        public SwitchOnStringNode() : base("Switch on String")
        {
            NodeType = NodeType.FlowControl;
            AddExecInput();
            AddDataInput("Selection", NodeScriptDataType.String, string.Empty);
            AddExecOutput("Default");
        }

        public NodeScriptPin AddCase(string value)
        {
            CaseValues.Add(value ?? string.Empty);
            return AddExecOutput($"Case {value}");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var sel = EvaluateInputAs<string>("Selection", ctx) ?? string.Empty;
            int idx = CaseValues.IndexOf(sel);
            if (idx >= 0)
            {
                var casePins = OutputExecPins.Where(p => p.Name != "Default").ToList();
                if (idx < casePins.Count)
                {
                    ctx.Executor.ExecuteChain(casePins[idx], ctx);
                    return null;
                }
            }
            ctx.Executor.ExecuteChain(GetOutputExecPin("Default"), ctx);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第五部分：函数 / 操作节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 读变量节点（纯）：从蓝图实例的局部变量表读取指定名称的变量值。
    /// </summary>
    public sealed class GetVariableNode : NodeScriptNode
    {
        public override bool IsPure => true;

        /// <summary>变量名。</summary>
        public string VariableName { get; set; } = "Variable";

        /// <summary>变量类型。</summary>
        public NodeScriptDataType VariableType { get; set; } = NodeScriptDataType.Float;

        public GetVariableNode() : base("Get Variable")
        {
            NodeType = NodeType.Variable;
            AddDataOutput("Value", NodeScriptDataType.Float);
        }

        public override object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx)
        {
            if (pin.Name != "Value") return null;
            return ctx.Instance.GetVariable(VariableName, NodeScriptTypeUtils.GetDefault(VariableType));
        }
    }

    /// <summary>
    /// 写变量节点（执行型）：将输入值写入实例局部变量表，并从 Then 继续。
    /// </summary>
    public sealed class SetVariableNode : NodeScriptNode
    {
        /// <summary>变量名。</summary>
        public string VariableName { get; set; } = "Variable";

        /// <summary>变量类型。</summary>
        public NodeScriptDataType VariableType { get; set; } = NodeScriptDataType.Float;

        public SetVariableNode() : base("Set Variable")
        {
            NodeType = NodeType.Variable;
            AddExecInput();
            AddDataInput("Value", NodeScriptDataType.Float);
            AddExecOutput("Then");
            AddDataOutput("Value", NodeScriptDataType.Float);
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var val = EvaluateInput("Value", ctx);
            ctx.Instance.SetVariable(VariableName, val);
            var outPin = GetOutputPin("Value");
            if (outPin != null) outPin.CachedValue = val;
            return GetOutputExecPin("Then");
        }
    }

    /// <summary>
    /// 数学运算节点（纯）：支持加减乘除、取模、比较、逻辑运算。
    /// 两输入 A/B，一输出 Result。
    /// </summary>
    public sealed class MathOperatorNode : NodeScriptNode
    {
        /// <summary>运算类型。</summary>
        public enum OpKind
        {
            Add, Subtract, Multiply, Divide, Modulo,
            Equal, NotEqual, Greater, Less, GreaterEqual, LessEqual,
            And, Or, Not, Negate
        }

        public override bool IsPure => true;
        public OpKind Operation { get; set; } = OpKind.Add;

        public MathOperatorNode() : base("Math")
        {
            NodeType = NodeType.Operator;
            AddDataInput("A", NodeScriptDataType.Float, 0f);
            AddDataInput("B", NodeScriptDataType.Float, 0f);
            AddDataOutput("Result", NodeScriptDataType.Float);
        }

        public override object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx)
        {
            if (pin.Name != "Result") return null;

            // 一元运算
            if (Operation == OpKind.Not || Operation == OpKind.Negate)
            {
                var a = EvaluateInput("A", ctx);
                if (Operation == OpKind.Not)
                {
                    var b = a is bool bv ? bv : Convert.ToBoolean(a, CultureInfo.InvariantCulture);
                    return !b;
                }
                // Negate
                if (a is int iv) return -iv;
                if (a is float fv) return -fv;
                if (a is Vector3 vv) return -vv;
                return -Convert.ToSingle(a, CultureInfo.InvariantCulture);
            }

            var left = EvaluateInput("A", ctx);
            var right = EvaluateInput("B", ctx);

            // 向量运算
            if (left is Vector3 lv && right is Vector3 rv)
            {
                return Operation switch
                {
                    OpKind.Add => lv + rv,
                    OpKind.Subtract => lv - rv,
                    OpKind.Equal => lv == rv,
                    OpKind.NotEqual => lv != rv,
                    _ => base.EvaluateOutput(pin, ctx)
                };
            }

            // 数值运算（int/float 混合提升为 float）
            bool bothInt = left is int && right is int;
            if (bothInt)
            {
                int a = Convert.ToInt32(left, CultureInfo.InvariantCulture);
                int b = Convert.ToInt32(right, CultureInfo.InvariantCulture);
                return Operation switch
                {
                    OpKind.Add => a + b,
                    OpKind.Subtract => a - b,
                    OpKind.Multiply => a * b,
                    OpKind.Divide => b == 0 ? 0 : a / b,
                    OpKind.Modulo => b == 0 ? 0 : a % b,
                    OpKind.Equal => a == b,
                    OpKind.NotEqual => a != b,
                    OpKind.Greater => a > b,
                    OpKind.Less => a < b,
                    OpKind.GreaterEqual => a >= b,
                    OpKind.LessEqual => a <= b,
                    _ => base.EvaluateOutput(pin, ctx)
                };
            }

            // 布尔逻辑
            if (Operation == OpKind.And || Operation == OpKind.Or)
            {
                bool a = left is bool ba ? ba : Convert.ToBoolean(left, CultureInfo.InvariantCulture);
                bool b = right is bool bb ? bb : Convert.ToBoolean(right, CultureInfo.InvariantCulture);
                return Operation == OpKind.And ? a && b : a || b;
            }

            // 浮点通用
            float fa = Convert.ToSingle(left, CultureInfo.InvariantCulture);
            float fb = Convert.ToSingle(right, CultureInfo.InvariantCulture);
            return Operation switch
            {
                OpKind.Add => fa + fb,
                OpKind.Subtract => fa - fb,
                OpKind.Multiply => fa * fb,
                OpKind.Divide => Math.Abs(fb) < 1e-9f ? 0f : fa / fb,
                OpKind.Modulo => Math.Abs(fb) < 1e-9f ? 0f : fa % fb,
                OpKind.Equal => Math.Abs(fa - fb) < 1e-6f,
                OpKind.NotEqual => Math.Abs(fa - fb) >= 1e-6f,
                OpKind.Greater => fa > fb,
                OpKind.Less => fa < fb,
                OpKind.GreaterEqual => fa >= fb,
                OpKind.LessEqual => fa <= fb,
                _ => base.EvaluateOutput(pin, ctx)
            };
        }
    }

    /// <summary>
    /// 调用 C# 方法节点（执行型）：通过反射在目标对象上调用指定方法。
    /// 目标可为实例变量、实体组件或任意对象。结果输出到 Return Value。
    /// </summary>
    public sealed class CallFunctionNode : NodeScriptNode
    {
        /// <summary>目标类型全名（静态方法或构造目标类型）。</summary>
        public string TargetTypeName { get; set; } = string.Empty;

        /// <summary>方法名。</summary>
        public string MethodName { get; set; } = string.Empty;

        /// <summary>是否静态方法。</summary>
        public bool IsStatic { get; set; }

        /// <summary>参数类型名列表（用于重载解析）。</summary>
        public List<NodeScriptDataType> ParameterTypes { get; } = new() { NodeScriptDataType.Float };

        public CallFunctionNode() : base("Call Function")
        {
            NodeType = NodeType.Function;
            AddExecInput();
            AddDataInput("Target", NodeScriptDataType.Object);
            AddDataInput("Arg 0", NodeScriptDataType.Float);
            AddDataOutput("Return Value", NodeScriptDataType.Object);
            AddExecOutput("Then");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            try
            {
                object? target = null;
                Type? targetType = null;

                if (!string.IsNullOrEmpty(TargetTypeName))
                {
                    targetType = NodeScriptRuntime.FindType(TargetTypeName);
                }

                if (!IsStatic)
                {
                    target = EvaluateInput("Target", ctx);
                    if (target != null) targetType = target.GetType();
                }

                if (targetType == null)
                {
                    ctx.Executor.ReportError(ctx, this, $"CallFunction: 无法解析目标类型 '{TargetTypeName}'。");
                    return GetOutputExecPin("Then");
                }

                // 收集参数
                var argPins = InputPins.Where(p => !p.IsExec && p.Name != "Target").ToList();
                var args = new object?[argPins.Count];
                var argTypes = new Type[argPins.Count];
                for (int i = 0; i < argPins.Count; i++)
                {
                    args[i] = EvaluateInput(argPins[i].Name, ctx);
                    argTypes[i] = NodeScriptTypeUtils.GetClrType(argPins[i].DataType);
                }

                var method = NodeScriptRuntime.FindMethod(targetType, MethodName, argTypes);
                if (method == null)
                {
                    ctx.Executor.ReportError(ctx, this, $"CallFunction: 未找到方法 '{MethodName}'。");
                    return GetOutputExecPin("Then");
                }

                var result = method.Invoke(target, args);
                var retPin = GetOutputPin("Return Value");
                if (retPin != null) retPin.CachedValue = result;
            }
            catch (Exception ex)
            {
                ctx.Executor.ReportError(ctx, this, $"CallFunction 异常: {ex.InnerException?.Message ?? ex.Message}");
            }
            return GetOutputExecPin("Then");
        }
    }

    /// <summary>
    /// 读取 ECS 实体组件属性节点（纯）：通过反射读取指定实体上指定组件的属性/字段。
    /// </summary>
    public sealed class GetEntityPropertyNode : NodeScriptNode
    {
        public override bool IsPure => true;

        /// <summary>组件类型全名。</summary>
        public string ComponentTypeName { get; set; } = "YamaEngine.ECS.TransformComponent";

        /// <summary>属性/字段名。</summary>
        public string PropertyName { get; set; } = "Position";

        public GetEntityPropertyNode() : base("Get Entity Property")
        {
            NodeType = NodeType.Function;
            AddDataInput("Entity", NodeScriptDataType.Entity);
            AddDataOutput("Value", NodeScriptDataType.Object);
        }

        public override object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx)
        {
            if (pin.Name != "Value") return null;
            var entity = EvaluateInputAs<int>("Entity", ctx);
            return NodeScriptRuntime.GetEntityProperty(ctx.ECS, entity, ComponentTypeName, PropertyName);
        }
    }

    /// <summary>
    /// 写入 ECS 实体组件属性节点（执行型）：通过反射设置属性并写回 ECS。
    /// </summary>
    public sealed class SetEntityPropertyNode : NodeScriptNode
    {
        public string ComponentTypeName { get; set; } = "YamaEngine.ECS.TransformComponent";
        public string PropertyName { get; set; } = "Position";

        public SetEntityPropertyNode() : base("Set Entity Property")
        {
            NodeType = NodeType.Function;
            AddExecInput();
            AddDataInput("Entity", NodeScriptDataType.Entity);
            AddDataInput("Value", NodeScriptDataType.Object);
            AddExecOutput("Then");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var entity = EvaluateInputAs<int>("Entity", ctx);
            var value = EvaluateInput("Value", ctx);
            NodeScriptRuntime.SetEntityProperty(ctx.ECS, entity, ComponentTypeName, PropertyName, value);
            return GetOutputExecPin("Then");
        }
    }

    /// <summary>
    /// 生成实体节点（执行型）：在 ECS 中创建新实体，输出其 ID。
    /// 可选输入 Name 与 Position。
    /// </summary>
    public sealed class SpawnEntityNode : NodeScriptNode
    {
        public SpawnEntityNode() : base("Spawn Entity")
        {
            NodeType = NodeType.Function;
            AddExecInput();
            AddDataInput("Name", NodeScriptDataType.String, "NewEntity");
            AddDataInput("Position", NodeScriptDataType.Vector3, Vector3.Zero);
            AddDataOutput("Entity", NodeScriptDataType.Entity);
            AddExecOutput("Then");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var name = EvaluateInputAs<string>("Name", ctx) ?? "NewEntity";
            var pos = EvaluateInputAs<Vector3>("Position", ctx);
            int id = ctx.ECS.CreateEntity(name, pos);
            var outPin = GetOutputPin("Entity");
            if (outPin != null) outPin.CachedValue = id;
            return GetOutputExecPin("Then");
        }
    }

    /// <summary>
    /// 销毁实体节点（执行型）。
    /// </summary>
    public sealed class DestroyEntityNode : NodeScriptNode
    {
        public DestroyEntityNode() : base("Destroy Entity")
        {
            NodeType = NodeType.Function;
            AddExecInput();
            AddDataInput("Entity", NodeScriptDataType.Entity);
            AddExecOutput("Then");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var entity = EvaluateInputAs<int>("Entity", ctx);
            if (ctx.ECS.HasEntity(entity))
                ctx.ECS.DestroyEntity(entity);
            return GetOutputExecPin("Then");
        }
    }

    /// <summary>
    /// 获取 Actor 节点（纯）：返回当前蓝图所属 Actor，或按名在世界中查找。
    /// </summary>
    public sealed class GetActorNode : NodeScriptNode
    {
        public override bool IsPure => true;

        public GetActorNode() : base("Get Actor")
        {
            NodeType = NodeType.Function;
            AddDataInput("Name", NodeScriptDataType.String, string.Empty);
            AddDataOutput("Actor", NodeScriptDataType.Object);
        }

        public override object? EvaluateOutput(NodeScriptPin pin, NodeScriptExecutionContext ctx)
        {
            if (pin.Name != "Actor") return null;
            var name = EvaluateInputAs<string>("Name", ctx) ?? string.Empty;
            if (!string.IsNullOrEmpty(name) && ctx.World != null)
            {
                return ctx.World.Actors.FirstOrDefault(a => a.Name == name);
            }
            return ctx.Actor;
        }
    }

    /// <summary>
    /// 设置 Actor 属性节点（执行型）：设置 Position/Rotation/Scale/Name。
    /// </summary>
    public sealed class SetActorPropertyNode : NodeScriptNode
    {
        /// <summary>属性名。</summary>
        public string PropertyName { get; set; } = "Position";

        public SetActorPropertyNode() : base("Set Actor Property")
        {
            NodeType = NodeType.Function;
            AddExecInput();
            AddDataInput("Actor", NodeScriptDataType.Object);
            AddDataInput("Value", NodeScriptDataType.Object);
            AddExecOutput("Then");
        }

        public override NodeScriptPin? Execute(NodeScriptExecutionContext ctx)
        {
            var actorObj = EvaluateInput("Actor", ctx) ?? ctx.Actor;
            var value = EvaluateInput("Value", ctx);
            if (actorObj is Actor actor)
            {
                switch (PropertyName)
                {
                    case "Position":
                        if (value is Vector3 p) actor.Position = p;
                        break;
                    case "Rotation":
                        if (value is Quaternion r) actor.Rotation = r;
                        break;
                    case "Scale":
                        if (value is Vector3 s) actor.Scale = s;
                        break;
                    case "Name":
                        if (value is string n) actor.Name = n;
                        break;
                }
            }
            return GetOutputExecPin("Then");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第六部分：蓝图执行引擎
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图执行上下文：单次事件触发的运行时环境。
    /// 持有 ECS、引擎系统、世界、当前实例与实体、局部调用栈与事件参数。
    /// </summary>
    public sealed class NodeScriptExecutionContext
    {
        /// <summary>ECS 实例。</summary>
        public EntityComponentSystem ECS { get; set; } = null!;

        /// <summary>引擎系统（时间/输入/日志等）。</summary>
        public EngineSystems Engine { get; set; } = null!;

        /// <summary>玩法世界（可为空，若蓝图不依赖 Actor）。</summary>
        public GameplayWorld? World { get; set; }

        /// <summary>执行器引用（用于子链执行、延迟调度、错误上报）。</summary>
        public NodeScriptExecutor Executor { get; set; } = null!;

        /// <summary>当前蓝图实例。</summary>
        public NodeScriptInstance Instance { get; set; } = null!;

        /// <summary>蓝图所附加的 ECS 实体 ID。</summary>
        public int EntityId { get; set; }

        /// <summary>蓝图所附加的 Actor（若通过 ActorComponent 运行）。</summary>
        public Actor? Actor { get; set; }

        /// <summary>当前帧 DeltaTime（OnTick 用）。</summary>
        public float DeltaTime { get; set; }

        /// <summary>事件参数（按引脚名索引）。</summary>
        public Dictionary<string, object?> EventArguments { get; set; } = new();

        /// <summary>调用栈（节点 ID），用于递归保护与调试。</summary>
        public Stack<Guid> CallStack { get; set; } = new();

        /// <summary>当前嵌套深度（执行 + 数据求值共用），超过阈值中断。</summary>
        public int Depth { get; set; }

        /// <summary>当前节点的入口输入引脚（多执行输入节点如 Gate/DoOnce 用于判定来源）。</summary>
        public NodeScriptPin? CurrentInputPin { get; set; }
    }

    /// <summary>
    /// 蓝图执行器：核心运行时。负责：
    ///   · 注册/管理蓝图实例
    ///   · 从事件入口触发并遍历执行图（执行流推动）
    ///   · 数据流拉取求值（递归求值输出引脚）
    ///   · 延迟续接调度（DelayNode）
    ///   · 递归深度保护与错误捕获上报
    ///   · 每帧分发 OnTick 事件
    /// </summary>
    public sealed class NodeScriptExecutor
    {
        /// <summary>最大嵌套深度，防止无限递归。</summary>
        public const int MaxDepth = 256;

        /// <summary>最大循环迭代数，防止死循环。</summary>
        public const int MaxLoopIterations = 1_000_000;

        private readonly EntityComponentSystem _ecs;
        private readonly EngineSystems _engine;
        private GameplayWorld? _world;

        /// <summary>已注册的实例列表（实例 + 实体 + Actor）。</summary>
        private readonly List<RegisteredInstance> _instances = new();

        /// <summary>待触发的延迟续接。</summary>
        private readonly List<PendingContinuation> _pending = new();

        /// <summary>引擎累计时间（秒），用于延迟调度。</summary>
        private double _engineTime;

        /// <summary>最近一次错误信息（调试用）。</summary>
        public string? LastError { get; private set; }

        /// <summary>错误事件，可供编辑器/日志订阅。</summary>
        public event Action<NodeScriptInstance, NodeScriptNode, Exception>? OnNodeError;

        public NodeScriptExecutor(EntityComponentSystem ecs, EngineSystems engine)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>绑定玩法世界（用于 Actor 相关节点）。</summary>
        public void BindWorld(GameplayWorld? world) => _world = world;

        // ── 实例管理 ──────────────────────────────────────────────────────────────

        /// <summary>注册一个蓝图实例到指定实体。</summary>
        public void RegisterInstance(NodeScriptInstance instance, int entityId, Actor? actor = null)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (_instances.Any(r => r.Instance == instance)) return;
            _instances.Add(new RegisteredInstance { Instance = instance, EntityId = entityId, Actor = actor });
        }

        /// <summary>取消注册。</summary>
        public void UnregisterInstance(NodeScriptInstance instance)
        {
            _instances.RemoveAll(r => r.Instance == instance);
        }

        /// <summary>判断实体是否已注册蓝图。</summary>
        public bool IsRegistered(int entityId) => _instances.Any(r => r.EntityId == entityId);

        /// <summary>获取实体上的首个蓝图实例。</summary>
        public NodeScriptInstance? GetInstance(int entityId) =>
            _instances.FirstOrDefault(r => r.EntityId == entityId)?.Instance;

        /// <summary>已注册实例数。</summary>
        public int InstanceCount => _instances.Count;

        // ── 事件触发 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 触发指定实例上的命名事件。查找图中的同名事件节点，
        /// 构造上下文并从其 "Then" 输出执行引脚开始遍历执行链。
        /// </summary>
        public void TriggerEvent(NodeScriptInstance instance, string eventName,
            int entityId, float dt, Dictionary<string, object?>? args = null, Actor? actor = null)
        {
            if (instance == null || string.IsNullOrEmpty(eventName)) return;
            var graph = instance.Asset.Graph;
            var eventNode = graph.FindEvent(eventName);
            if (eventNode == null) return; // 该蓝图无此事件

            graph.ResetAllCaches();
            var ctx = new NodeScriptExecutionContext
            {
                ECS = _ecs,
                Engine = _engine,
                World = _world,
                Executor = this,
                Instance = instance,
                EntityId = entityId,
                Actor = actor,
                DeltaTime = dt,
                EventArguments = args ?? new Dictionary<string, object?>(),
                CallStack = new Stack<Guid>(),
                Depth = 0
            };

            var startPin = eventNode.GetOutputExecPin("Then");
            ExecuteChain(startPin, ctx);
        }

        // ── 执行流遍历 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 从指定输出执行引脚开始，沿连线遍历执行节点链。
        /// 每个节点的 Execute 返回下一个要跟随的输出执行引脚；
        /// 返回 null 或无连线时链终止。循环/分支节点会递归调用本方法执行子链。
        /// </summary>
        public void ExecuteChain(NodeScriptPin? startOutputExecPin, NodeScriptExecutionContext ctx)
        {
            var current = startOutputExecPin;
            while (current != null)
            {
                if (ctx.Depth > MaxDepth)
                {
                    ReportError(ctx, null, $"执行深度超过上限 {MaxDepth}，已中断以防栈溢出。");
                    return;
                }

                var graph = ctx.Instance.Asset.Graph;
                // 找到从当前输出执行引脚连入的目标输入引脚
                var link = graph.Links.FirstOrDefault(l => l.SourcePinId == current.Id);
                if (link == null) return; // 无连线，链终止

                var targetNode = graph.FindNode(link.TargetNodeId);
                if (targetNode == null) return;

                var targetInputPin = graph.FindPin(link.TargetPinId);

                ctx.CallStack.Push(targetNode.Id);
                ctx.CurrentInputPin = targetInputPin;
                ctx.Depth++;

                NodeScriptPin? next = null;
                try
                {
                    next = targetNode.Execute(ctx);
                }
                catch (Exception ex)
                {
                    ReportError(ctx, targetNode, ex);
                    next = null;
                }
                finally
                {
                    ctx.Depth--;
                    if (ctx.CallStack.Count > 0) ctx.CallStack.Pop();
                }

                current = next;
            }
        }

        // ── 数据流求值 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 求值一个输入数据引脚的值（pull-based）。
        /// 若有连线，递归求值源输出引脚所在节点的 EvaluateOutput；
        /// 若无连线，返回引脚默认字面量。
        /// </summary>
        public object? EvaluateDataPin(NodeScriptPin inputPin, NodeScriptExecutionContext ctx)
        {
            if (inputPin == null) return null;
            var graph = ctx.Instance.Asset.Graph;

            var link = graph.Links.FirstOrDefault(l => l.TargetPinId == inputPin.Id);
            if (link != null)
            {
                var sourceNode = graph.FindNode(link.SourceNodeId);
                var sourcePin = sourceNode?.OutputPins.FirstOrDefault(p => p.Id == link.SourcePinId);
                if (sourceNode != null && sourcePin != null)
                {
                    if (ctx.Depth > MaxDepth)
                    {
                        ReportError(ctx, sourceNode, $"数据求值深度超过上限 {MaxDepth}，已中断。");
                        return null;
                    }
                    ctx.Depth++;
                    object? val = null;
                    try
                    {
                        val = sourceNode.EvaluateOutput(sourcePin, ctx);
                    }
                    catch (Exception ex)
                    {
                        ReportError(ctx, sourceNode, ex);
                    }
                    finally
                    {
                        ctx.Depth--;
                    }
                    // 类型兼容转换
                    if (val != null && !NodeScriptTypeUtils.GetClrType(inputPin.DataType).IsInstanceOfType(val))
                    {
                        if (NodeScriptTypeUtils.TryConvert(val, inputPin.DataType, out var converted))
                            return converted;
                    }
                    return val;
                }
            }
            return inputPin.DefaultValue;
        }

        // ── 延迟调度 ──────────────────────────────────────────────────────────────

        /// <summary>调度一个延迟续接：到时间后从指定输出执行引脚恢复执行。</summary>
        public void ScheduleContinuation(NodeScriptExecutionContext ctx, Guid outputPinId, float delaySeconds)
        {
            _pending.Add(new PendingContinuation
            {
                Instance = ctx.Instance,
                EntityId = ctx.EntityId,
                Actor = ctx.Actor,
                OutputPinId = outputPinId,
                TriggerTime = _engineTime + Math.Max(0, delaySeconds),
                EventArgs = new Dictionary<string, object?>(ctx.EventArguments)
            });
        }

        // ── 每帧更新 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 每帧调用：推进时间、处理到期延迟续接、分发 OnTick 事件。
        /// </summary>
        public void Update(float dt)
        {
            _engineTime += dt;

            // 1. 处理到期延迟续接
            if (_pending.Count > 0)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var pc = _pending[i];
                    if (_engineTime >= pc.TriggerTime)
                    {
                        _pending.RemoveAt(i);
                        var graph = pc.Instance.Asset.Graph;
                        var pin = graph.FindPin(pc.OutputPinId);
                        if (pin != null && pin.IsExec)
                        {
                            var ctx = new NodeScriptExecutionContext
                            {
                                ECS = _ecs,
                                Engine = _engine,
                                World = _world,
                                Executor = this,
                                Instance = pc.Instance,
                                EntityId = pc.EntityId,
                                Actor = pc.Actor,
                                DeltaTime = 0f,
                                EventArguments = pc.EventArgs,
                                CallStack = new Stack<Guid>(),
                                Depth = 0
                            };
                            ExecuteChain(pin, ctx);
                        }
                    }
                }
            }

            // 2. 分发 OnTick 事件
            foreach (var reg in _instances)
            {
                var graph = reg.Instance.Asset.Graph;
                if (graph.FindEvent("OnTick") != null)
                {
                    TriggerEvent(reg.Instance, "OnTick", reg.EntityId, dt, null, reg.Actor);
                }
            }
        }

        // ── 错误上报 ──────────────────────────────────────────────────────────────

        /// <summary>上报节点执行错误。</summary>
        public void ReportError(NodeScriptExecutionContext ctx, NodeScriptNode? node, string message)
        {
            var nodeName = node?.Title ?? "<unknown>";
            LastError = $"[蓝图:{ctx.Instance?.Asset?.Name}] 节点 '{nodeName}': {message}";
            _engine.LogError(LastError);
        }

        /// <summary>上报节点执行异常。</summary>
        public void ReportError(NodeScriptExecutionContext ctx, NodeScriptNode? node, Exception ex)
        {
            var nodeName = node?.Title ?? "<unknown>";
            LastError = $"[蓝图:{ctx.Instance?.Asset?.Name}] 节点 '{nodeName}' 异常: {ex.Message}";
            _engine.LogError(LastError);
            OnNodeError?.Invoke(ctx.Instance!, node!, ex);
        }

        // ── 内部数据结构 ──────────────────────────────────────────────────────────

        private sealed class RegisteredInstance
        {
            public NodeScriptInstance Instance = null!;
            public int EntityId;
            public Actor? Actor;
        }

        private sealed class PendingContinuation
        {
            public NodeScriptInstance Instance = null!;
            public int EntityId;
            public Actor? Actor;
            public Guid OutputPinId;
            public double TriggerTime;
            public Dictionary<string, object?> EventArgs = new();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第七部分：蓝图资产、实例与组件
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图资产：可序列化的图结构容器。支持继承（ParentAsset），
    /// 子蓝图可扩展父蓝图的节点与变量。
    /// </summary>
    public sealed class NodeScriptAsset
    {
        /// <summary>资产唯一标识。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>蓝图名称。</summary>
        public string Name { get; set; } = "NewNodeScript";

        /// <summary>蓝图描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>蓝图图结构。</summary>
        public NodeScriptGraph Graph { get; set; } = new();

        /// <summary>蓝图变量定义表（变量名 → 类型）。</summary>
        public Dictionary<string, NodeScriptDataType> VariableDefinitions { get; } = new();

        /// <summary>父蓝图 ID（继承），可为空。</summary>
        public Guid? ParentAssetId { get; set; }

        /// <summary>默认变量值表（变量名 → 盒装默认值）。</summary>
        public Dictionary<string, object?> DefaultVariableValues { get; } = new();

        /// <summary>添加变量定义。</summary>
        public void AddVariable(string name, NodeScriptDataType type, object? defaultValue = null)
        {
            VariableDefinitions[name] = type;
            DefaultVariableValues[name] = defaultValue ?? NodeScriptTypeUtils.GetDefault(type);
        }

        /// <summary>合并父蓝图的变量与节点（继承解析）。</summary>
        public void ApplyInheritance(NodeScriptAsset? parent)
        {
            if (parent == null) return;
            foreach (var kv in parent.VariableDefinitions)
            {
                if (!VariableDefinitions.ContainsKey(kv.Key))
                {
                    VariableDefinitions[kv.Key] = kv.Value;
                    DefaultVariableValues[kv.Key] = parent.DefaultVariableValues.TryGetValue(kv.Key, out var v) ? v : null;
                }
            }
        }
    }

    /// <summary>
    /// 蓝图运行时实例：持有资产引用与实例局部状态（变量值 + 节点状态）。
    /// 同一资产可创建多个独立实例。
    /// </summary>
    public sealed class NodeScriptInstance
    {
        /// <summary>对应的蓝图资产。</summary>
        public NodeScriptAsset Asset { get; }

        /// <summary>实例局部变量值表（变量名 → 值）。</summary>
        public Dictionary<string, object?> Variables { get; } = new();

        /// <summary>节点状态表（如 DoOnce 标志、Gate 开关），键为 "节点Id::状态名"。</summary>
        public Dictionary<string, object?> State { get; } = new();

        /// <summary>实例是否已激活。</summary>
        public bool IsActive { get; set; } = true;

        public NodeScriptInstance(NodeScriptAsset asset)
        {
            Asset = asset ?? throw new ArgumentNullException(nameof(asset));
            // 用资产默认值初始化变量
            foreach (var kv in asset.DefaultVariableValues)
                Variables[kv.Key] = kv.Value;
        }

        /// <summary>读取变量（不存在则返回默认）。</summary>
        public object? GetVariable(string name, object? defaultValue = null) =>
            Variables.TryGetValue(name, out var v) ? v : defaultValue;

        /// <summary>写入变量。</summary>
        public void SetVariable(string name, object? value) => Variables[name] = value;

        /// <summary>读取节点状态。</summary>
        public T GetState<T>(string key, T defaultValue)
        {
            if (State.TryGetValue(key, out var v) && v is T t) return t;
            return defaultValue;
        }

        /// <summary>写入节点状态。</summary>
        public void SetState(string key, object? value) => State[key] = value;

        /// <summary>重置实例状态（变量回默认，清空节点状态）。</summary>
        public void Reset()
        {
            Variables.Clear();
            foreach (var kv in Asset.DefaultVariableValues)
                Variables[kv.Key] = kv.Value;
            State.Clear();
        }
    }

    /// <summary>
    /// ECS 蓝图组件（值类型，实现 IComponent）。
    /// 附加到 ECS 实体以标记其携带蓝图。实际运行时实例由
    /// <see cref="NodeScriptSystem"/> 扫描本组件并交由执行器管理。
    /// </summary>
    public struct NodeScriptComponent : IComponent
    {
        /// <summary>蓝图资产 ID（名称或 Guid 字符串）。</summary>
        public string NodeScriptAssetId;

        /// <summary>是否自动在 BeginPlay 时启动。</summary>
        public bool AutoStart;

        /// <summary>是否启用 OnTick。</summary>
        public bool TickEnabled;

        public NodeScriptComponent(string assetId, bool autoStart = true, bool tickEnabled = true)
        {
            NodeScriptAssetId = assetId ?? string.Empty;
            AutoStart = autoStart;
            TickEnabled = tickEnabled;
        }
    }

    /// <summary>
    /// Gameplay 层蓝图组件（OOP，继承 ActorComponent）。
    /// 将 Actor 生命周期事件桥接到蓝图事件系统。
    /// 引擎启动时应设置 <see cref="SharedExecutor"/> 与 <see cref="SharedWorld"/>。
    /// </summary>
    public sealed class NodeScriptActorComponent : ActorComponent
    {
        /// <summary>共享执行器（引擎启动时设置）。</summary>
        public static NodeScriptExecutor? SharedExecutor { get; set; }

        /// <summary>共享玩法世界（引擎启动时设置）。</summary>
        public static GameplayWorld? SharedWorld { get; set; }

        /// <summary>本组件使用的蓝图资产。</summary>
        public NodeScriptAsset? Asset { get; set; }

        /// <summary>运行时实例。</summary>
        public NodeScriptInstance? Instance { get; private set; }

        /// <summary>是否已注册到执行器。</summary>
        public bool IsRegistered { get; private set; }

        public NodeScriptActorComponent()
        {
            Name = "NodeScript";
            TickEnabled = true;
        }

        protected override void OnBeginPlay()
        {
            if (Asset == null || SharedExecutor == null) return;
            Instance = new NodeScriptInstance(Asset);
            SharedExecutor.RegisterInstance(Instance, 0, Owner);
            IsRegistered = true;
            // 触发 OnBeginPlay 事件（实体 ID 0 表示 Actor 模式）
            SharedExecutor.TriggerEvent(Instance, "OnBeginPlay", 0, 0f, null, Owner);
        }

        protected override void OnTick(float deltaTime)
        {
            // OnTick 由执行器的 Update 统一分发；这里仅保证实例已激活
        }

        protected override void OnEndPlay()
        {
            if (Instance != null && SharedExecutor != null && IsRegistered)
            {
                SharedExecutor.TriggerEvent(Instance, "OnEndPlay", 0, 0f, null, Owner);
                SharedExecutor.UnregisterInstance(Instance);
                IsRegistered = false;
            }
        }
    }

    /// <summary>
    /// 蓝图系统（实现 ISystem）：扫描 ECS 中携带 <see cref="NodeScriptComponent"/> 的实体，
    /// 自动创建实例并交由执行器管理生命周期与每帧更新。
    /// </summary>
    public sealed class NodeScriptSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "NodeScriptSystem";

        private readonly NodeScriptExecutor _executor;
        private readonly EntityComponentSystem _ecs;
        private readonly EngineSystems _engine;

        /// <summary>暴露执行器供外部触发事件。</summary>
        public NodeScriptExecutor Executor => _executor;

        public NodeScriptSystem(EntityComponentSystem ecs, EngineSystems engine)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _executor = new NodeScriptExecutor(ecs, engine);
        }

        /// <summary>每帧更新：同步新实体 + 推进执行器。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 1. 扫描新增的带蓝图组件实体并初始化
            foreach (var entityId in ecs.QueryWithComponent<NodeScriptComponent>())
            {
                if (_executor.IsRegistered(entityId)) continue;
                var comp = ecs.GetComponent<NodeScriptComponent>(entityId);
                var asset = NodeScriptAssetDatabase.Get(comp.NodeScriptAssetId);
                if (asset == null)
                {
                    _engine.LogWarning($"蓝图系统: 实体 {entityId} 引用的蓝图资产 '{comp.NodeScriptAssetId}' 未注册。");
                    continue;
                }
                var instance = new NodeScriptInstance(asset);
                _executor.RegisterInstance(instance, entityId);
                if (comp.AutoStart)
                {
                    _executor.TriggerEvent(instance, "OnBeginPlay", entityId, 0f);
                }
            }

            // 2. 推进执行器（延迟续接 + OnTick 分发）
            _executor.Update(dt);
        }
    }

    /// <summary>
    /// 蓝图资产数据库：按 ID/名称注册与查找蓝图资产。
    /// 供 <see cref="NodeScriptSystem"/> 与编辑器使用。
    /// </summary>
    public static class NodeScriptAssetDatabase
    {
        private static readonly Dictionary<string, NodeScriptAsset> _byName = new(StringComparer.Ordinal);
        private static readonly Dictionary<Guid, NodeScriptAsset> _byId = new();

        /// <summary>注册资产。</summary>
        public static void Register(NodeScriptAsset asset)
        {
            if (asset == null) return;
            _byName[asset.Name] = asset;
            _byId[asset.Id] = asset;
        }

        /// <summary>按 ID 或名称查找。</summary>
        public static NodeScriptAsset? Get(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            if (_byName.TryGetValue(idOrName, out var a)) return a;
            if (Guid.TryParse(idOrName, out var guid) && _byId.TryGetValue(guid, out var b)) return b;
            return null;
        }

        /// <summary>按 Guid 查找。</summary>
        public static NodeScriptAsset? Get(Guid id) => _byId.TryGetValue(id, out var a) ? a : null;

        /// <summary>取消注册。</summary>
        public static bool Unregister(string name)
        {
            if (!_byName.TryGetValue(name, out var a)) return false;
            _byName.Remove(name);
            _byId.Remove(a.Id);
            return true;
        }

        /// <summary>清空数据库。</summary>
        public static void Clear()
        {
            _byName.Clear();
            _byId.Clear();
        }

        /// <summary>所有已注册资产。</summary>
        public static IReadOnlyCollection<NodeScriptAsset> All => _byName.Values;
    }

    /// <summary>
    /// 节点工厂：按工厂名称创建节点实例，用于反序列化与编辑器拖放。
    /// 支持注册自定义节点类型。
    /// </summary>
    public static class NodeScriptNodeFactory
    {
        private static readonly Dictionary<string, Func<NodeScriptNode>> _creators = new(StringComparer.Ordinal);

        static NodeScriptNodeFactory()
        {
            RegisterDefaults();
        }

        /// <summary>注册一个节点构造器。</summary>
        public static void Register(string factoryName, Func<NodeScriptNode> creator)
        {
            if (string.IsNullOrEmpty(factoryName) || creator == null) return;
            _creators[factoryName] = creator;
        }

        /// <summary>按工厂名创建节点。</summary>
        public static NodeScriptNode? Create(string factoryName)
        {
            if (string.IsNullOrEmpty(factoryName)) return null;
            return _creators.TryGetValue(factoryName, out var c) ? c() : null;
        }

        private static void RegisterDefaults()
        {
            // 事件节点
            Register(nameof(OnBeginPlayNode), () => new OnBeginPlayNode());
            Register(nameof(OnEndPlayNode), () => new OnEndPlayNode());
            Register(nameof(OnTickNode), () => new OnTickNode());
            Register(nameof(OnCollisionNode), () => new OnCollisionNode());
            Register(nameof(OnOverlapNode), () => new OnOverlapNode());
            Register(nameof(OnCustomEventNode), () => new OnCustomEventNode());
            Register(nameof(OnKeyPressedNode), () => new OnKeyPressedNode());
            Register(nameof(OnKeyReleasedNode), () => new OnKeyReleasedNode());
            Register(nameof(OnAxisInputNode), () => new OnAxisInputNode());
            Register(nameof(OnTakeDamageNode), () => new OnTakeDamageNode());
            Register(nameof(OnDeathNode), () => new OnDeathNode());
            Register(nameof(OnSpawnNode), () => new OnSpawnNode());
            Register(nameof(OnInteractNode), () => new OnInteractNode());
            // 流程控制
            Register(nameof(BranchNode), () => new BranchNode());
            Register(nameof(SequenceNode), () => new SequenceNode());
            Register(nameof(WhileLoopNode), () => new WhileLoopNode());
            Register(nameof(ForLoopNode), () => new ForLoopNode());
            Register(nameof(ForEachLoopNode), () => new ForEachLoopNode());
            Register(nameof(GateNode), () => new GateNode());
            Register(nameof(DelayNode), () => new DelayNode());
            Register(nameof(DoOnceNode), () => new DoOnceNode());
            Register(nameof(SwitchOnIntNode), () => new SwitchOnIntNode());
            Register(nameof(SwitchOnStringNode), () => new SwitchOnStringNode());
            // 函数/操作
            Register(nameof(GetVariableNode), () => new GetVariableNode());
            Register(nameof(SetVariableNode), () => new SetVariableNode());
            Register(nameof(MathOperatorNode), () => new MathOperatorNode());
            Register(nameof(CallFunctionNode), () => new CallFunctionNode());
            Register(nameof(GetEntityPropertyNode), () => new GetEntityPropertyNode());
            Register(nameof(SetEntityPropertyNode), () => new SetEntityPropertyNode());
            Register(nameof(SpawnEntityNode), () => new SpawnEntityNode());
            Register(nameof(DestroyEntityNode), () => new DestroyEntityNode());
            Register(nameof(GetActorNode), () => new GetActorNode());
            Register(nameof(SetActorPropertyNode), () => new SetActorPropertyNode());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第八部分：蓝图运行时反射助手（供执行器与编译器共享）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图运行时反射助手：封装对 ECS 组件的非泛型反射访问、
    /// 类型与方法查找，供执行型节点与编译产物调用。
    /// </summary>
    public static class NodeScriptRuntime
    {
        /// <summary>按全名查找类型（扫描所有已加载程序集）。</summary>
        public static Type? FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t;
                try { t = asm.GetType(fullName); }
                catch { continue; }
                if (t != null) return t;
            }
            // 简短名兜底
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == fullName) return t;
                }
            }
            return null;
        }

        /// <summary>查找方法（按名与参数类型，含基类查找）。</summary>
        public static MethodInfo? FindMethod(Type type, string methodName, Type[] argTypes)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            foreach (var m in type.GetMethods(Flags))
            {
                if (m.Name != methodName) continue;
                var ps = m.GetParameters();
                if (ps.Length != argTypes.Length) continue;
                bool match = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!ps[i].ParameterType.IsAssignableFrom(argTypes[i]) &&
                        !NodeScriptTypeUtils.AreCompatible(ToNodeScriptType(argTypes[i]), ToNodeScriptType(ps[i].ParameterType)))
                    {
                        match = false; break;
                    }
                }
                if (match) return m;
            }
            return null;
        }

        /// <summary>非泛型读取 ECS 实体组件属性。</summary>
        public static object? GetEntityProperty(EntityComponentSystem ecs, int entityId, string componentTypeName, string propertyName)
        {
            var compType = FindType(componentTypeName);
            if (compType == null) return null;
            if (!typeof(IComponent).IsAssignableFrom(compType)) return null;

            var getMethod = typeof(EntityComponentSystem).GetMethods()
                .First(m => m.Name == nameof(EntityComponentSystem.GetComponent) && m.IsGenericMethod)
                .MakeGenericMethod(compType);
            var comp = getMethod.Invoke(ecs, new object[] { entityId });
            if (comp == null) return null;

            var prop = compType.GetProperty(propertyName);
            if (prop != null) return prop.GetValue(comp);
            var field = compType.GetField(propertyName);
            return field?.GetValue(comp);
        }

        /// <summary>非泛型写入 ECS 实体组件属性（读出→改→写回）。</summary>
        public static void SetEntityProperty(EntityComponentSystem ecs, int entityId, string componentTypeName, string propertyName, object? value)
        {
            var compType = FindType(componentTypeName);
            if (compType == null) return;
            if (!typeof(IComponent).IsAssignableFrom(compType)) return;

            var getMethod = typeof(EntityComponentSystem).GetMethods()
                .First(m => m.Name == nameof(EntityComponentSystem.GetComponent) && m.IsGenericMethod)
                .MakeGenericMethod(compType);
            var comp = getMethod.Invoke(ecs, new object[] { entityId });
            if (comp == null) return;

            var prop = compType.GetProperty(propertyName);
            if (prop != null && prop.CanWrite)
            {
                if (value != null && !prop.PropertyType.IsInstanceOfType(value))
                    NodeScriptTypeUtils.TryConvert(value, ToNodeScriptType(prop.PropertyType), out value);
                prop.SetValue(comp, value);
            }
            else
            {
                var field = compType.GetField(propertyName);
                if (field != null)
                {
                    if (value != null && !field.FieldType.IsInstanceOfType(value))
                        NodeScriptTypeUtils.TryConvert(value, ToNodeScriptType(field.FieldType), out value);
                    field.SetValue(comp, value);
                }
            }

            var setMethod = typeof(EntityComponentSystem).GetMethods()
                .First(m => m.Name == nameof(EntityComponentSystem.SetComponent) && m.IsGenericMethod)
                .MakeGenericMethod(compType);
            setMethod.Invoke(ecs, new object[] { entityId, comp });
        }

        private static NodeScriptDataType ToNodeScriptType(Type t)
        {
            if (t == typeof(bool)) return NodeScriptDataType.Bool;
            if (t == typeof(int)) return NodeScriptDataType.Int;
            if (t == typeof(float)) return NodeScriptDataType.Float;
            if (t == typeof(string)) return NodeScriptDataType.String;
            if (t == typeof(Vector3)) return NodeScriptDataType.Vector3;
            if (t == typeof(Quaternion)) return NodeScriptDataType.Quaternion;
            return NodeScriptDataType.Object;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  第九部分：蓝图编译器（生成 C# 源码 + Roslyn 集成）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图编译器：将蓝图图结构编译为 C# 源码（派生自 <see cref="ScriptComponent"/>），
    /// 并可经 <see cref="ScriptEngine"/> 的 Roslyn 编译器编译为程序集，实现性能优化。
    ///
    /// 编译策略：
    ///   · 事件入口节点 → 对应的生命周期方法重写（OnBeginPlay/OnUpdate/...）。
    ///   · 执行链 → 顺序语句；纯数据节点 → 内联表达式。
    ///   · 分支 → if/else；循环 → while/for；顺序 → 内联展开。
    ///   · ECS/Actor 操作 → 调用基类属性与 NodeScriptRuntime 助手。
    ///   · 不支持的节点 → 生成注释占位（运行时仍可由执行器兜底）。
    /// </summary>
    public static class NodeScriptCompiler
    {
        /// <summary>
        /// 生成 C# 源码字符串。
        /// </summary>
        /// <param name="asset">蓝图资产。</param>
        /// <param name="className">生成的类名。</param>
        /// <param name="ns">命名空间。</param>
        public static string GenerateCSharp(NodeScriptAsset asset, string className, string ns = "YamaCompiledNodeScripts")
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrEmpty(className)) className = "CompiledNodeScript";
            className = SanitizeIdentifier(className);

            var sb = new StringBuilder();
            sb.AppendLine("// ════════════════════════════════════════════════════════════════════════");
            sb.AppendLine("// 此文件由yama蓝图编译器自动生成，请勿手动编辑。");
            sb.AppendLine($"// 源蓝图: {asset.Name}  (Id: {asset.Id})");
            sb.AppendLine("// ════════════════════════════════════════════════════════════════════════");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Numerics;");
            sb.AppendLine("using YamaEngine.ECS;");
            sb.AppendLine("using YamaEngine.Scripting;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>编译后的蓝图: {asset.Name}</summary>");
            sb.AppendLine($"    public sealed class {className} : ScriptComponent");
            sb.AppendLine("    {");

            // 变量字段
            foreach (var kv in asset.VariableDefinitions)
            {
                var clrType = NodeScriptTypeUtils.GetClrType(kv.Value);
                var fieldName = "_var_" + SanitizeIdentifier(kv.Key);
                var defaultExpr = EmitLiteral(asset.DefaultVariableValues.TryGetValue(kv.Key, out var dv) ? dv : NodeScriptTypeUtils.GetDefault(kv.Value), kv.Value);
                sb.AppendLine($"        private {PrettyType(clrType)} {fieldName} = {defaultExpr};");
            }
            sb.AppendLine();

            var graph = asset.Graph;

            // OnBeginPlay
            if (graph.FindEvent("OnBeginPlay") != null)
            {
                sb.AppendLine("        public override void Awake()");
                sb.AppendLine("        {");
                sb.AppendLine("            base.Awake();");
                EmitEventBody(sb, graph, "OnBeginPlay", asset, "            ");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // OnTick → Update
            if (graph.FindEvent("OnTick") != null)
            {
                sb.AppendLine("        public override void Update(float deltaTime)");
                sb.AppendLine("        {");
                sb.AppendLine("            base.Update(deltaTime);");
                var ctx = new EmitContext(asset);
                // OnTick 的 DeltaSeconds 输出引脚绑定到 deltaTime
                var tickNode = graph.FindEvent("OnTick");
                var dsPin = tickNode?.GetOutputPin("Delta Seconds");
                if (dsPin != null) ctx.PinExpr[dsPin.Id] = "deltaTime";
                EmitChain(sb, graph, tickNode?.GetOutputExecPin("Then"), ctx, "            ");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // OnEndPlay
            if (graph.FindEvent("OnEndPlay") != null)
            {
                sb.AppendLine("        public override void OnDestroy()");
                sb.AppendLine("        {");
                sb.AppendLine("            base.OnDestroy();");
                EmitEventBody(sb, graph, "OnEndPlay", asset, "            ");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // 自定义事件入口（作为 public 方法）
            foreach (var ev in graph.EventNodes.Where(n => n is OnCustomEventNode))
            {
                var methodName = "Event_" + SanitizeIdentifier(ev.EventName);
                sb.AppendLine($"        public void {methodName}()");
                sb.AppendLine("        {");
                EmitChain(sb, graph, ev.GetOutputExecPin("Then"), new EmitContext(asset), "            ");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// 编译蓝图为程序集（经 ScriptEngine 的 Roslyn 编译器）。
        /// </summary>
        public static CompileResult? CompileWithRoslyn(NodeScriptAsset asset, ScriptEngine engine,
            string className, string ns = "YamaCompiledNodeScripts")
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            var source = GenerateCSharp(asset, className, ns);
            var filePath = $"{ns}.{className}.cs";
            return engine.CompileSource(source, $"{ns}.{className}", filePath);
        }

        // ── 编译内部实现 ──────────────────────────────────────────────────────────

        private static void EmitEventBody(StringBuilder sb, NodeScriptGraph graph, string eventName, NodeScriptAsset asset, string indent)
        {
            var ev = graph.FindEvent(eventName);
            if (ev == null)
            {
                sb.AppendLine($"{indent}// 无 {eventName} 事件");
                return;
            }
            EmitChain(sb, graph, ev.GetOutputExecPin("Then"), new EmitContext(asset), indent);
        }

        /// <summary>编译上下文：记录引脚到 C# 表达式的映射与已生成临时变量。</summary>
        private sealed class EmitContext
        {
            public NodeScriptAsset Asset;
            public Dictionary<Guid, string> PinExpr = new();
            public Dictionary<Guid, string> PinTempVar = new();
            public int TempCounter;

            public EmitContext(NodeScriptAsset asset) { Asset = asset; }

            public string NewTemp(string prefix = "tmp")
            {
                return $"{prefix}_{++TempCounter}";
            }
        }

        /// <summary>从输出执行引脚开始，沿链发射 C# 语句。</summary>
        private static void EmitChain(StringBuilder sb, NodeScriptGraph graph, NodeScriptPin? startPin, EmitContext ctx, string indent)
        {
            var current = startPin;
            int safety = 0;
            while (current != null)
            {
                if (++safety > 10000) { sb.AppendLine($"{indent}// 编译器: 链过长，已截断"); return; }
                var link = graph.Links.FirstOrDefault(l => l.SourcePinId == current.Id);
                if (link == null) return;
                var node = graph.FindNode(link.TargetNodeId);
                if (node == null) return;

                var next = EmitNode(sb, graph, node, ctx, indent);
                current = next;
            }
        }

        /// <summary>发射单个节点，返回下一个要跟随的输出执行引脚。</summary>
        private static NodeScriptPin? EmitNode(StringBuilder sb, NodeScriptGraph graph, NodeScriptNode node, EmitContext ctx, string indent)
        {
            switch (node)
            {
                case GetVariableNode gv:
                    {
                        var field = "_var_" + SanitizeIdentifier(gv.VariableName);
                        var outPin = gv.GetOutputPin("Value");
                        if (outPin != null) ctx.PinExpr[outPin.Id] = field;
                        return null;
                    }

                case SetVariableNode sv:
                    {
                        var field = "_var_" + SanitizeIdentifier(sv.VariableName);
                        var valExpr = EmitExpr(graph, sv.GetInputPin("Value"), ctx, sb, indent);
                        sb.AppendLine($"{indent}{field} = {valExpr};");
                        var outPin = sv.GetOutputPin("Value");
                        if (outPin != null) ctx.PinExpr[outPin.Id] = field;
                        return sv.GetOutputExecPin("Then");
                    }

                case MathOperatorNode math:
                    {
                        var a = EmitExpr(graph, math.GetInputPin("A"), ctx, sb, indent);
                        var b = EmitExpr(graph, math.GetInputPin("B"), ctx, sb, indent);
                        var op = math.Operation;
                        var outPin = math.GetOutputPin("Result");
                        if (outPin != null)
                        {
                            string expr = op switch
                            {
                                MathOperatorNode.OpKind.Add => $"({a} + {b})",
                                MathOperatorNode.OpKind.Subtract => $"({a} - {b})",
                                MathOperatorNode.OpKind.Multiply => $"({a} * {b})",
                                MathOperatorNode.OpKind.Divide => $"({a} / {b})",
                                MathOperatorNode.OpKind.Modulo => $"({a} % {b})",
                                MathOperatorNode.OpKind.Equal => $"({a} == {b})",
                                MathOperatorNode.OpKind.NotEqual => $"({a} != {b})",
                                MathOperatorNode.OpKind.Greater => $"({a} > {b})",
                                MathOperatorNode.OpKind.Less => $"({a} < {b})",
                                MathOperatorNode.OpKind.GreaterEqual => $"({a} >= {b})",
                                MathOperatorNode.OpKind.LessEqual => $"({a} <= {b})",
                                MathOperatorNode.OpKind.And => $"({a} && {b})",
                                MathOperatorNode.OpKind.Or => $"({a} || {b})",
                                MathOperatorNode.OpKind.Not => $"(!{a})",
                                MathOperatorNode.OpKind.Negate => $"(-{a})",
                                _ => "default"
                            };
                            ctx.PinExpr[outPin.Id] = expr;
                        }
                        return null;
                    }

                case BranchNode br:
                    {
                        var cond = EmitExpr(graph, br.GetInputPin("Condition"), ctx, sb, indent);
                        sb.AppendLine($"{indent}if ({cond})");
                        sb.AppendLine($"{indent}{{");
                        EmitChain(sb, graph, br.GetOutputExecPin("True"), ctx, indent + "    ");
                        sb.AppendLine($"{indent}}}");
                        var falsePin = br.GetOutputExecPin("False");
                        if (falsePin != null && graph.Links.Any(l => l.SourcePinId == falsePin.Id))
                        {
                            sb.AppendLine($"{indent}else");
                            sb.AppendLine($"{indent}{{");
                            EmitChain(sb, graph, falsePin, ctx, indent + "    ");
                            sb.AppendLine($"{indent}}}");
                        }
                        return null;
                    }

                case WhileLoopNode wl:
                    {
                        var cond = EmitExpr(graph, wl.GetInputPin("Condition"), ctx, sb, indent);
                        sb.AppendLine($"{indent}while ({cond})");
                        sb.AppendLine($"{indent}{{");
                        EmitChain(sb, graph, wl.GetOutputExecPin("Loop Body"), ctx, indent + "    ");
                        sb.AppendLine($"{indent}}}");
                        return wl.GetOutputExecPin("Completed");
                    }

                case ForLoopNode fl:
                    {
                        var first = EmitExpr(graph, fl.GetInputPin("First Index"), ctx, sb, indent);
                        var last = EmitExpr(graph, fl.GetInputPin("Last Index"), ctx, sb, indent);
                        var idxVar = ctx.NewTemp("i");
                        sb.AppendLine($"{indent}for (int {idxVar} = {first}; {idxVar} <= {last}; {idxVar}++)");
                        sb.AppendLine($"{indent}{{");
                        var indexOut = fl.GetOutputPin("Index");
                        if (indexOut != null) ctx.PinExpr[indexOut.Id] = idxVar;
                        EmitChain(sb, graph, fl.GetOutputExecPin("Loop Body"), ctx, indent + "    ");
                        sb.AppendLine($"{indent}}}");
                        return fl.GetOutputExecPin("Completed");
                    }

                case SequenceNode seq:
                    {
                        foreach (var pin in seq.OutputExecPins)
                            EmitChain(sb, graph, pin, ctx, indent);
                        return null;
                    }

                case SpawnEntityNode sp:
                    {
                        var nameExpr = EmitExpr(graph, sp.GetInputPin("Name"), ctx, sb, indent);
                        var posExpr = EmitExpr(graph, sp.GetInputPin("Position"), ctx, sb, indent);
                        var temp = ctx.NewTemp("entity");
                        sb.AppendLine($"{indent}int {temp} = ECS.CreateEntity({nameExpr}, {posExpr});");
                        var outPin = sp.GetOutputPin("Entity");
                        if (outPin != null) ctx.PinExpr[outPin.Id] = temp;
                        return sp.GetOutputExecPin("Then");
                    }

                case DestroyEntityNode de:
                    {
                        var entExpr = EmitExpr(graph, de.GetInputPin("Entity"), ctx, sb, indent);
                        sb.AppendLine($"{indent}ECS.DestroyEntity({entExpr});");
                        return de.GetOutputExecPin("Then");
                    }

                case GetEntityPropertyNode gep:
                    {
                        var entExpr = EmitExpr(graph, gep.GetInputPin("Entity"), ctx, sb, indent);
                        var expr = $"NodeScriptRuntime.GetEntityProperty(ECS, {entExpr}, \"{Escape(gep.ComponentTypeName)}\", \"{Escape(gep.PropertyName)}\")";
                        var outPin = gep.GetOutputPin("Value");
                        if (outPin != null) ctx.PinExpr[outPin.Id] = expr;
                        return null;
                    }

                case SetEntityPropertyNode sep:
                    {
                        var entExpr = EmitExpr(graph, sep.GetInputPin("Entity"), ctx, sb, indent);
                        var valExpr = EmitExpr(graph, sep.GetInputPin("Value"), ctx, sb, indent);
                        sb.AppendLine($"{indent}NodeScriptRuntime.SetEntityProperty(ECS, {entExpr}, \"{Escape(sep.ComponentTypeName)}\", \"{Escape(sep.PropertyName)}\", {valExpr});");
                        return sep.GetOutputExecPin("Then");
                    }

                case GetActorNode ga:
                    {
                        var outPin = ga.GetOutputPin("Actor");
                        if (outPin != null) ctx.PinExpr[outPin.Id] = "this";
                        return null;
                    }

                case SetActorPropertyNode sap:
                    {
                        var valExpr = EmitExpr(graph, sap.GetInputPin("Value"), ctx, sb, indent);
                        sb.AppendLine($"{indent}// 设置 Actor 属性 {sap.PropertyName}");
                        switch (sap.PropertyName)
                        {
                            case "Position": sb.AppendLine($"{indent}Position = {valExpr};"); break;
                            case "Rotation": sb.AppendLine($"{indent}Rotation = {valExpr};"); break;
                            case "Scale": sb.AppendLine($"{indent}Scale = {valExpr};"); break;
                            case "Name": sb.AppendLine($"{indent}EntityName = {valExpr};"); break;
                        }
                        return sap.GetOutputExecPin("Then");
                    }

                default:
                    sb.AppendLine($"{indent}// 未降低的节点: {node.Title} ({node.FactoryName}) — 运行时由执行器兜底");
                    // 尝试跟随第一个执行输出
                    return node.OutputExecPins.FirstOrDefault();
            }
        }

        /// <summary>发射输入数据引脚的 C# 表达式（内联纯节点，引用临时变量）。</summary>
        private static string EmitExpr(NodeScriptGraph graph, NodeScriptPin? inputPin, EmitContext ctx, StringBuilder sb, string indent)
        {
            if (inputPin == null) return "default";
            var link = graph.Links.FirstOrDefault(l => l.TargetPinId == inputPin.Id);
            if (link != null)
            {
                var srcNode = graph.FindNode(link.SourceNodeId);
                var srcPin = srcNode?.OutputPins.FirstOrDefault(p => p.Id == link.SourcePinId);
                if (srcNode != null && srcPin != null)
                {
                    // 已有表达式映射
                    if (ctx.PinExpr.TryGetValue(srcPin.Id, out var expr)) return expr;
                    // 纯节点：递归降低
                    if (srcNode.IsPure)
                    {
                        EmitNode(sb, graph, srcNode, ctx, indent);
                        if (ctx.PinExpr.TryGetValue(srcPin.Id, out expr)) return expr;
                    }
                    // 执行型节点的缓存：用一个局部变量
                    var temp = ctx.NewTemp("v");
                    sb.AppendLine($"{indent}var {temp} = default(object); // {srcNode.Title}");
                    return temp;
                }
            }
            // 无连线：字面量
            return EmitLiteral(inputPin.DefaultValue, inputPin.DataType);
        }

        /// <summary>发射字面量表达式。</summary>
        private static string EmitLiteral(object? value, NodeScriptDataType type)
        {
            return type switch
            {
                NodeScriptDataType.Bool => (value is bool b ? b : false) ? "true" : "false",
                NodeScriptDataType.Int => Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                NodeScriptDataType.Float => FloatLiteral(value),
                NodeScriptDataType.String => "\"" + Escape(value as string ?? string.Empty) + "\"",
                NodeScriptDataType.Vector3 => value is Vector3 v
                    ? $"new Vector3({FloatLiteral(v.X)}f, {FloatLiteral(v.Y)}f, {FloatLiteral(v.Z)}f)"
                    : "Vector3.Zero",
                NodeScriptDataType.Quaternion => "Quaternion.Identity",
                NodeScriptDataType.Entity => Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                _ => "null"
            };
        }

        private static string FloatLiteral(object? value)
        {
            var f = Convert.ToSingle(value ?? 0f, CultureInfo.InvariantCulture);
            return f.ToString("R", CultureInfo.InvariantCulture) + "f";
        }

        private static string PrettyType(Type t) => t == typeof(float) ? "float" :
            t == typeof(int) ? "int" : t == typeof(bool) ? "bool" :
            t == typeof(string) ? "string" : t == typeof(Vector3) ? "Vector3" :
            t == typeof(Quaternion) ? "Quaternion" : t.FullName ?? "object";

        private static string SanitizeIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string Escape(string? s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
