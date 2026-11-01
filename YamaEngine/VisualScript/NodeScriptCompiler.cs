using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YamaEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  yama引擎 · 蓝图编译器 (NodeScript Compiler)
    //  ────────────────────────────────────────────────────────────────────────────
    //  完整的蓝图编译管线，包含：
    //    · IROpCode        — 中间表示操作码枚举
    //    · IRInstruction   — IR 指令（OpCode/操作数/寄存器）
    //    · IRGenerator     — IR 生成器（节点图 → 三地址码 IR）
    //    · IROptimizer     — IR 优化器（常量折叠/死代码消除/公共子表达式消除/强度削弱/循环不变量外提）
    //    · CSharpCodeGenerator — C# 代码生成（IR → C# 源代码）
    //    · NodeScriptCompiler     — 编译管线（节点图 → IR → 优化 → C#）
    //    · CompileMessage  — 编译错误/警告消息
    //    · 增量编译支持
    //  作者：yama引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  IROpCode - 中间表示操作码
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 中间表示（IR）操作码。
    /// </summary>
    public enum IROpCode
    {
        /// <summary>空操作。</summary>
        Nop,

        // ── 加载 / 存储 ──
        /// <summary>加载常量到寄存器。</summary>
        LoadConst,
        /// <summary>从变量加载。</summary>
        LoadVar,
        /// <summary>存储到变量。</summary>
        StoreVar,
        /// <summary>从内存地址加载。</summary>
        Load,
        /// <summary>存储到内存地址。</summary>
        Store,

        // ── 算术运算 ──
        /// <summary>加法。</summary>
        Add,
        /// <summary>减法。</summary>
        Sub,
        /// <summary>乘法。</summary>
        Mul,
        /// <summary>除法。</summary>
        Div,
        /// <summary>取模。</summary>
        Mod,
        /// <summary>幂运算。</summary>
        Pow,
        /// <summary>取负。</summary>
        Neg,

        // ── 数学函数 ──
        /// <summary>平方根。</summary>
        Sqrt,
        /// <summary>绝对值。</summary>
        Abs,
        /// <summary>正弦。</summary>
        Sin,
        /// <summary>余弦。</summary>
        Cos,
        /// <summary>正切。</summary>
        Tan,
        /// <summary>反正切2。</summary>
        Atan2,
        /// <summary>向上取整。</summary>
        Ceil,
        /// <summary>向下取整。</summary>
        Floor,
        /// <summary>四舍五入。</summary>
        Round,
        /// <summary>取小数部分。</summary>
        Fract,
        /// <summary>最小值。</summary>
        Min,
        /// <summary>最大值。</summary>
        Max,
        /// <summary>钳制。</summary>
        Clamp,
        /// <summary>线性插值。</summary>
        Lerp,

        // ── 比较 ──
        /// <summary>等于。</summary>
        CmpEq,
        /// <summary>不等于。</summary>
        CmpNe,
        /// <summary>大于。</summary>
        CmpGt,
        /// <summary>小于。</summary>
        CmpLt,
        /// <summary>大于等于。</summary>
        CmpGe,
        /// <summary>小于等于。</summary>
        CmpLe,

        // ── 位运算 ──
        /// <summary>按位与。</summary>
        And,
        /// <summary>按位或。</summary>
        Or,
        /// <summary>按位异或。</summary>
        Xor,
        /// <summary>按位取反。</summary>
        Not,

        // ── 逻辑运算 ──
        /// <summary>逻辑与。</summary>
        LogicAnd,
        /// <summary>逻辑或。</summary>
        LogicOr,
        /// <summary>逻辑非。</summary>
        LogicNot,

        // ── 跳转 / 分支 ──
        /// <summary>条件分支。</summary>
        Branch,
        /// <summary>无条件跳转。</summary>
        Jump,
        /// <summary>跳转到标签。</summary>
        JumpLabel,
        /// <summary>函数调用。</summary>
        Call,
        /// <summary>返回。</summary>
        Return,

        // ── 成员访问 ──
        /// <summary>获取成员。</summary>
        GetMember,
        /// <summary>设置成员。</summary>
        SetMember,

        // ── 数组 / 索引 ──
        /// <summary>索引访问。</summary>
        Index,
        /// <summary>数组长度。</summary>
        ArrayLen,
        /// <summary>数组添加。</summary>
        ArrayAdd,
        /// <summary>数组移除。</summary>
        ArrayRemove,

        // ── 向量 / 旋转 / 变换 ──
        /// <summary>向量加法。</summary>
        Vec3Add,
        /// <summary>向量减法。</summary>
        Vec3Sub,
        /// <summary>向量缩放。</summary>
        Vec3Scale,
        /// <summary>向量点乘。</summary>
        Vec3Dot,
        /// <summary>向量叉乘。</summary>
        Vec3Cross,
        /// <summary>向量归一化。</summary>
        Vec3Normalize,
        /// <summary>向量长度。</summary>
        Vec3Length,
        /// <summary>线性插值（向量）。</summary>
        Vec3Lerp,
        /// <summary>构造向量。</summary>
        Vec3Make,
        /// <summary>拆解向量。</summary>
        Vec3Break,
        /// <summary>球面线性插值。</summary>
        Slerp,

        // ── 类型转换 ──
        /// <summary>类型转换。</summary>
        Cast,

        // ── 特殊 ──
        /// <summary>打印字符串。</summary>
        PrintString,
        /// <summary>延迟。</summary>
        Delay,
        /// <summary>设置定时器。</summary>
        SetTimer,
        /// <summary>生成 Actor。</summary>
        SpawnActor,
        /// <summary>销毁 Actor。</summary>
        DestroyActor,
        /// <summary>施加伤害。</summary>
        ApplyDamage
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  IRInstruction - IR 指令
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 中间表示指令：单条三地址码指令。
    /// </summary>
    [Serializable]
    public class IRInstruction
    {
        /// <summary>操作码。</summary>
        public IROpCode OpCode { get; set; }

        /// <summary>目标寄存器（结果存储位置）。</summary>
        public string Destination { get; set; } = string.Empty;

        /// <summary>源操作数 1。</summary>
        public string Operand1 { get; set; } = string.Empty;

        /// <summary>源操作数 2。</summary>
        public string Operand2 { get; set; } = string.Empty;

        /// <summary>源操作数 3（可选）。</summary>
        public string Operand3 { get; set; } = string.Empty;

        /// <summary>常量值（当 OpCode == LoadConst 时使用）。</summary>
        public string? ConstantValue { get; set; }

        /// <summary>常量数据类型。</summary>
        public GraphPinDataType ConstDataType { get; set; } = GraphPinDataType.Float;

        /// <summary>源节点 ID（用于调试和错误报告）。</summary>
        public Guid SourceNodeId { get; set; }

        /// <summary>源引脚 ID（用于调试和错误报告）。</summary>
        public Guid SourcePinId { get; set; }

        /// <summary>标签名称（用于 JumpLabel/Call）。</summary>
        public string? Label { get; set; }

        /// <summary>注释（用于 IR 可读性）。</summary>
        public string? Comment { get; set; }

        /// <summary>指令在 IR 列表中的索引。</summary>
        public int Index { get; set; }

        /// <summary>
        /// 创建常量加载指令。
        /// </summary>
        public static IRInstruction LoadConstant(string dest, string value, GraphPinDataType type, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = IROpCode.LoadConst,
                Destination = dest,
                ConstantValue = value,
                ConstDataType = type,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 创建二元运算指令。
        /// </summary>
        public static IRInstruction BinaryOp(IROpCode op, string dest, string src1, string src2, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = op,
                Destination = dest,
                Operand1 = src1,
                Operand2 = src2,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 创建一元运算指令。
        /// </summary>
        public static IRInstruction UnaryOp(IROpCode op, string dest, string src, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = op,
                Destination = dest,
                Operand1 = src,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 创建分支指令。
        /// </summary>
        public static IRInstruction BranchInst(string condition, string trueLabel, string falseLabel, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = IROpCode.Branch,
                Operand1 = condition,
                Label = trueLabel,
                Operand2 = falseLabel,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 创建跳转标签。
        /// </summary>
        public static IRInstruction LabelInst(string label)
        {
            return new IRInstruction
            {
                OpCode = IROpCode.JumpLabel,
                Label = label
            };
        }

        /// <summary>
        /// 创建函数调用指令。
        /// </summary>
        public static IRInstruction CallInst(string functionName, string dest, string[] args, Guid nodeId = default)
        {
            var inst = new IRInstruction
            {
                OpCode = IROpCode.Call,
                Destination = dest,
                Operand1 = functionName,
                SourceNodeId = nodeId
            };
            // 使用 Label 字段存储以逗号分隔的参数列表
            if (args.Length > 0)
                inst.Label = string.Join(",", args);
            return inst;
        }

        /// <summary>
        /// 创建变量加载指令。
        /// </summary>
        public static IRInstruction LoadVarInst(string varName, string dest, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = IROpCode.LoadVar,
                Operand1 = varName,
                Destination = dest,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 创建变量存储指令。
        /// </summary>
        public static IRInstruction StoreVarInst(string varName, string src, Guid nodeId = default)
        {
            return new IRInstruction
            {
                OpCode = IROpCode.StoreVar,
                Destination = varName,
                Operand1 = src,
                SourceNodeId = nodeId
            };
        }

        /// <summary>
        /// 转换为可读的文本表示。
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Comment))
            {
                sb.Append($"; {Comment}\n");
            }

            switch (OpCode)
            {
                case IROpCode.Nop:
                    sb.Append("NOP");
                    break;
                case IROpCode.LoadConst:
                    sb.Append($"{ConstDataType} {Destination} = {ConstantValue}");
                    break;
                case IROpCode.LoadVar:
                    sb.Append($"{Destination} = LOAD {Operand1}");
                    break;
                case IROpCode.StoreVar:
                    sb.Append($"STORE {Destination} = {Operand1}");
                    break;
                case IROpCode.Branch:
                    sb.Append($"BRANCH {Operand1} ? {Label} : {Operand2}");
                    break;
                case IROpCode.Jump:
                    sb.Append($"JUMP {Label}");
                    break;
                case IROpCode.JumpLabel:
                    sb.Append($"LABEL {Label}:");
                    break;
                case IROpCode.Call:
                    var args = Label ?? "";
                    sb.Append(string.IsNullOrEmpty(Destination) ?
                        $"CALL {Operand1}({args})" :
                        $"{Destination} = CALL {Operand1}({args})");
                    break;
                case IROpCode.Return:
                    sb.Append(string.IsNullOrEmpty(Operand1) ?
                        "RETURN" :
                        $"RETURN {Operand1}");
                    break;
                default:
                    // 二元/一元操作
                    if (!string.IsNullOrEmpty(Operand2))
                        sb.Append($"{Destination} = {Operand1} {OpCode} {Operand2}");
                    else if (!string.IsNullOrEmpty(Operand1))
                        sb.Append($"{Destination} = {OpCode}({Operand1})");
                    else
                        sb.Append($"{OpCode} {Destination}");
                    break;
            }

            return sb.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  IRGenerator - IR 生成器
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 中间表示生成器：将节点图转换为三地址码 IR。
    /// </summary>
    public class IRGenerator
    {
        /// <summary>生成的 IR 指令列表。</summary>
        public List<IRInstruction> Instructions { get; private set; } = new List<IRInstruction>();

        /// <summary>编译消息列表。</summary>
        public List<GraphValidationMessage> Messages { get; private set; } = new List<GraphValidationMessage>();

        /// <summary>寄存器计数器。</summary>
        private int _registerCounter;

        /// <summary>标签计数器。</summary>
        private int _labelCounter;

        /// <summary>值名称映射：引脚 ID → 寄存器/变量名。</summary>
        private Dictionary<Guid, string> _pinValues = new Dictionary<Guid, string>();

        /// <summary>节点输出映射：节点 ID → 输出寄存器。</summary>
        private Dictionary<Guid, string> _nodeOutputs = new Dictionary<Guid, string>();

        /// <summary>
        /// 从节点图生成 IR。
        /// </summary>
        public List<IRInstruction> Generate(NodeGraph graph)
        {
            Instructions = new List<IRInstruction>();
            Messages = new List<GraphValidationMessage>();
            _registerCounter = 0;
            _labelCounter = 0;
            _pinValues.Clear();
            _nodeOutputs.Clear();

            // 加载变量初始值
            foreach (var variable in graph.Variables)
            {
                if (!string.IsNullOrEmpty(variable.DefaultValue))
                {
                    var reg = NewRegister();
                    Instructions.Add(IRInstruction.LoadConstant(reg, variable.DefaultValue, variable.DataType));
                    Instructions.Add(IRInstruction.StoreVarInst(SanitizeName(variable.Name), reg));
                }
            }

            // 为每个事件生成 IR
            foreach (var evt in graph.Events)
            {
                GenerateEventIR(evt, graph);
            }

            // 为每个函数生成 IR
            foreach (var func in graph.Functions)
            {
                GenerateFunctionIR(func, graph);
            }

            return Instructions;
        }

        /// <summary>
        /// 为事件生成 IR。
        /// </summary>
        private void GenerateEventIR(GraphEvent evt, NodeGraph graph)
        {
            var label = SanitizeName(evt.EventName);
            Instructions.Add(new IRInstruction
            {
                OpCode = IROpCode.JumpLabel,
                Label = label,
                Comment = $"Event: {evt.EventName}"
            });

            var startNode = graph.GetNode(evt.NodeId);
            if (startNode != null)
            {
                GenerateExecutionFlowIR(startNode, graph);
            }

            Instructions.Add(new IRInstruction { OpCode = IROpCode.Return });
            Instructions.Add(new IRInstruction { OpCode = IROpCode.Nop }); // 分隔
        }

        /// <summary>
        /// 为函数生成 IR。
        /// </summary>
        private void GenerateFunctionIR(GraphFunction func, NodeGraph graph)
        {
            var funcLabel = SanitizeName(func.FunctionName);
            Instructions.Add(new IRInstruction
            {
                OpCode = IROpCode.JumpLabel,
                Label = $"FUNC_{funcLabel}",
                Comment = $"Function: {func.FunctionName}"
            });

            // 加载参数
            foreach (var pin in func.InputPins.Where(p => p.DataType != GraphPinDataType.Exec))
            {
                var reg = NewRegister();
                Instructions.Add(IRInstruction.LoadVarInst($"param_{SanitizeName(pin.Name)}", reg));
                _pinValues[pin.Id] = reg;
            }

            var entryNode = graph.GetNode(func.EntryNodeId);
            if (entryNode != null)
            {
                GenerateExecutionFlowIR(entryNode, graph);
            }

            Instructions.Add(new IRInstruction { OpCode = IROpCode.Return });
            Instructions.Add(new IRInstruction { OpCode = IROpCode.Nop });
        }

        /// <summary>
        /// 为执行流生成 IR（递归跟踪执行引脚）。
        /// </summary>
        private void GenerateExecutionFlowIR(GraphNode node, NodeGraph graph)
        {
            var visited = new HashSet<Guid>();
            GenerateExecFlowRecursive(node, graph, visited);
        }

        private void GenerateExecFlowRecursive(GraphNode node, NodeGraph graph, HashSet<Guid> visited)
        {
            if (visited.Contains(node.Id)) return;
            visited.Add(node.Id);

            // 解析输入引脚值
            foreach (var pin in node.InputPins.Where(p => p.DataType != GraphPinDataType.Exec))
            {
                if (!_pinValues.ContainsKey(pin.Id))
                {
                    var reg = ResolvePinValue(pin, graph);
                    _pinValues[pin.Id] = reg;
                }
            }

            // 生成节点 IR
            GenerateNodeIR(node, graph);

            // 传递输出值到下一个节点的输入引脚
            PassOutputValues(node, graph);

            // 跟踪执行流
            var execOutput = node.OutputPins.FirstOrDefault(p => p.DataType == GraphPinDataType.Exec);
            if (execOutput != null)
            {
                foreach (var connId in execOutput.ConnectionIds)
                {
                    var conn = graph.GetConnection(connId);
                    if (conn != null)
                    {
                        var nextNode = graph.GetNode(conn.TargetNodeId);
                        if (nextNode != null)
                        {
                            GenerateExecFlowRecursive(nextNode, graph, visited);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 解析引脚值（从连接源获取或使用默认值）。
        /// </summary>
        private string ResolvePinValue(GraphPin pin, NodeGraph graph)
        {
            if (pin.IsConnected)
            {
                foreach (var connId in pin.ConnectionIds)
                {
                    var conn = graph.GetConnection(connId);
                    if (conn != null)
                    {
                        var srcNode = graph.GetNode(conn.SourceNodeId);
                        if (srcNode != null)
                        {
                            var srcPin = srcNode.GetPin(conn.SourcePinId);
                            if (srcPin != null && _pinValues.TryGetValue(srcPin.Id, out var srcValue))
                            {
                                return srcValue;
                            }
                        }
                    }
                }
            }

            // 使用默认值
            var reg = NewRegister();
            var defaultValue = !string.IsNullOrEmpty(pin.DefaultValue) ? pin.DefaultValue : GetDefaultStr(pin.DataType);
            Instructions.Add(IRInstruction.LoadConstant(reg, defaultValue, pin.DataType, pin.OwnerNodeId));
            return reg;
        }

        /// <summary>
        /// 生成单个节点的 IR。
        /// </summary>
        private void GenerateNodeIR(GraphNode node, NodeGraph graph)
        {
            var destReg = NewRegister();

            switch (node.NodeType)
            {
                // 数学运算
                case "Add":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Add, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Subtract":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Sub, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Multiply":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Mul, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Divide":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Div, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Modulo":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Mod, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Power":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Pow, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Sqrt":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.Sqrt, destReg, GetInput(node, "Input"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Abs":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.Abs, destReg, GetInput(node, "Input"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Negate":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.Neg, destReg, GetInput(node, "Input"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Min":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Min, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Max":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Max, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Clamp":
                    var clampReg = NewRegister();
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Max, clampReg, GetInput(node, "Min"), GetInput(node, "Value"), node.Id));
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Min, destReg, clampReg, GetInput(node, "Max"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Lerp":
                    var lerpA = GetInput(node, "A");
                    var lerpB = GetInput(node, "B");
                    var lerpAlpha = GetInput(node, "Alpha");
                    var lerpTmp = NewRegister();
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Sub, lerpTmp, lerpB, lerpA, node.Id));
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Mul, lerpTmp, lerpTmp, lerpAlpha, node.Id));
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Add, destReg, lerpA, lerpTmp, node.Id));
                    SetOutput(node, "Result", destReg);
                    break;

                // 向量运算
                case "Vector3Add":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Vec3Add, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Vector3Subtract":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Vec3Sub, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Vector3Scale":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Vec3Scale, destReg, GetInput(node, "Vector"), GetInput(node, "Scale"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "CrossProduct":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Vec3Cross, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "DotProduct":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.Vec3Dot, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Normalize":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.Vec3Normalize, destReg, GetInput(node, "Input"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "Vector3Length":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.Vec3Length, destReg, GetInput(node, "Vector"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "MakeVector3":
                    Instructions.Add(new IRInstruction
                    {
                        OpCode = IROpCode.Vec3Make,
                        Destination = destReg,
                        Operand1 = GetInput(node, "X"),
                        Operand2 = GetInput(node, "Y"),
                        Operand3 = GetInput(node, "Z"),
                        SourceNodeId = node.Id
                    });
                    SetOutput(node, "Result", destReg);
                    break;

                // 比较
                case "Equal":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.CmpEq, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "ReturnValue", destReg);
                    break;
                case "NotEqual":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.CmpNe, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "ReturnValue", destReg);
                    break;
                case "Greater":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.CmpGt, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "ReturnValue", destReg);
                    break;
                case "Less":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.CmpLt, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "ReturnValue", destReg);
                    break;

                // 逻辑
                case "AND":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.LogicAnd, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "OR":
                    Instructions.Add(IRInstruction.BinaryOp(IROpCode.LogicOr, destReg, GetInput(node, "A"), GetInput(node, "B"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;
                case "NOT":
                    Instructions.Add(IRInstruction.UnaryOp(IROpCode.LogicNot, destReg, GetInput(node, "Input"), node.Id));
                    SetOutput(node, "Result", destReg);
                    break;

                // 流程控制
                case "Branch":
                    var trueLabel = NewLabel();
                    var falseLabel = NewLabel();
                    Instructions.Add(IRInstruction.BranchInst(GetInput(node, "Condition"), trueLabel, falseLabel, node.Id));
                    Instructions.Add(IRInstruction.LabelInst(trueLabel));
                    // True 分支
                    TrackExecBranch(node, "True", graph);
                    // False 分支
                    Instructions.Add(new IRInstruction { OpCode = IROpCode.Jump, Label = falseLabel });
                    Instructions.Add(IRInstruction.LabelInst(falseLabel));
                    TrackExecBranch(node, "False", graph);
                    break;

                // 变量操作
                case "SetVariable":
                    var varName = node.Metadata.GetValueOrDefault("VariableName", "var");
                    Instructions.Add(IRInstruction.StoreVarInst(SanitizeName(varName), GetInput(node, "Value"), node.Id));
                    // 输出值透传
                    _pinValues[node.OutputPins.FirstOrDefault(p => p.DataType != GraphPinDataType.Exec)?.Id ?? Guid.Empty] = GetInput(node, "Value");
                    break;

                // 函数调用
                case "CallFunction":
                    var funcName = node.Metadata.GetValueOrDefault("FunctionName", "Unknown");
                    var callArgs = node.InputPins
                        .Where(p => p.DataType != GraphPinDataType.Exec)
                        .Select(p => GetInput(node, p.Name))
                        .ToArray();
                    Instructions.Add(IRInstruction.CallInst(funcName, "", callArgs, node.Id));
                    break;

                // 打印
                case "PrintString":
                    Instructions.Add(new IRInstruction
                    {
                        OpCode = IROpCode.PrintString,
                        Operand1 = GetInput(node, "String"),
                        SourceNodeId = node.Id
                    });
                    break;

                // 默认处理
                default:
                    Messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Warning,
                        NodeId = node.Id,
                        Message = $"节点类型 '{node.NodeType}' 未实现 IR 生成",
                        Suggestion = "请为该节点类型添加 IR 生成逻辑"
                    });
                    break;
            }
        }

        /// <summary>
        /// 跟踪执行分支输出。
        /// </summary>
        private void TrackExecBranch(GraphNode node, string outputName, NodeGraph graph)
        {
            var execPin = node.GetOutputPin(outputName);
            if (execPin == null) return;

            foreach (var connId in execPin.ConnectionIds)
            {
                var conn = graph.GetConnection(connId);
                if (conn == null) continue;

                var nextNode = graph.GetNode(conn.TargetNodeId);
                if (nextNode != null)
                {
                    var visited = new HashSet<Guid>();
                    GenerateExecFlowRecursive(nextNode, graph, visited);
                }
            }
        }

        /// <summary>
        /// 将节点输出值传递到下一个节点的输入引脚。
        /// </summary>
        private void PassOutputValues(GraphNode node, NodeGraph graph)
        {
            foreach (var outputPin in node.OutputPins.Where(p => p.DataType != GraphPinDataType.Exec))
            {
                if (_pinValues.TryGetValue(outputPin.Id, out var value))
                {
                    foreach (var connId in outputPin.ConnectionIds)
                    {
                        var conn = graph.GetConnection(connId);
                        if (conn != null)
                        {
                            var nextNode = graph.GetNode(conn.TargetNodeId);
                            if (nextNode != null)
                            {
                                var targetPin = nextNode.GetPin(conn.TargetPinId);
                                if (targetPin != null)
                                {
                                    _pinValues[targetPin.Id] = value;
                                }
                            }
                        }
                    }
                }
            }
        }

        private string GetInput(GraphNode node, string pinName)
        {
            var pin = node.GetInputPin(pinName);
            if (pin == null) return NewRegister();
            return _pinValues.TryGetValue(pin.Id, out var val) ? val : NewRegister();
        }

        private void SetOutput(GraphNode node, string pinName, string value)
        {
            var pin = node.GetOutputPin(pinName);
            if (pin != null)
            {
                _pinValues[pin.Id] = value;
            }
        }

        private string NewRegister() => $"r{_registerCounter++}";
        private string NewLabel() => $"L{_labelCounter++}";

        private static string GetDefaultStr(GraphPinDataType type)
        {
            return type switch
            {
                GraphPinDataType.Bool => "false",
                GraphPinDataType.Int => "0",
                GraphPinDataType.Float => "0.0f",
                GraphPinDataType.Double => "0.0",
                GraphPinDataType.String => "\"\"",
                GraphPinDataType.Vector3 => "Vector3.Zero",
                GraphPinDataType.Quaternion => "Quaternion.Identity",
                GraphPinDataType.Object => "null",
                _ => "0"
            };
        }

        private static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.Length > 0 ? sb.ToString() : "_unnamed";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  IROptimizer - IR 优化器
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IR 优化器：对 IR 指令列表进行多种优化。
    /// </summary>
    public class IROptimizer
    {
        /// <summary>优化统计信息。</summary>
        public int ConstantFoldCount { get; private set; }
        public int DeadCodeCount { get; private set; }
        public int CSECount { get; private set; }
        public int StrengthReductionCount { get; private set; }
        public int LICMCount { get; private set; }

        /// <summary>
        /// 执行所有优化。
        /// </summary>
        public List<IRInstruction> Optimize(List<IRInstruction> instructions)
        {
            var result = instructions.ToList();

            // 反复应用优化直到不再变化
            bool changed;
            int maxPasses = 10;
            int pass = 0;

            do
            {
                changed = false;
                var before = result.Count;

                result = ConstantFolding(result);
                result = DeadCodeElimination(result);
                result = CommonSubexpressionElimination(result);
                result = StrengthReduction(result);
                // LICM 需要循环分析，简化处理

                if (result.Count != before) changed = true;
                pass++;
            } while (changed && pass < maxPasses);

            return result;
        }

        /// <summary>
        /// 常量折叠：将编译期可计算的常量表达式替换为结果。
        /// </summary>
        public List<IRInstruction> ConstantFolding(List<IRInstruction> instructions)
        {
            var result = new List<IRInstruction>();
            var values = new Dictionary<string, string>();

            foreach (var inst in instructions)
            {
                if (inst.OpCode == IROpCode.LoadConst && !string.IsNullOrEmpty(inst.ConstantValue))
                {
                    values[inst.Destination] = inst.ConstantValue;
                    result.Add(inst);
                    continue;
                }

                if (inst.OpCode == IROpCode.LoadVar)
                {
                    result.Add(inst);
                    continue;
                }

                // 检查操作数是否都为常量
                if (IsBinaryMathOp(inst.OpCode) &&
                    values.TryGetValue(inst.Operand1, out var val1) &&
                    values.TryGetValue(inst.Operand2, out var val2))
                {
                    var computed = EvaluateConstantBinary(inst.OpCode, val1, val2);
                    if (computed != null)
                    {
                        var constInst = IRInstruction.LoadConstant(inst.Destination, computed, inst.ConstDataType, inst.SourceNodeId);
                        constInst.Comment = $"[常量折叠] {inst.Operand1} {inst.OpCode} {inst.Operand2}";
                        values[inst.Destination] = computed;
                        result.Add(constInst);
                        ConstantFoldCount++;
                        continue;
                    }
                }

                result.Add(inst);
            }

            return result;
        }

        /// <summary>
        /// 死代码消除：移除未被使用的指令。
        /// </summary>
        public List<IRInstruction> DeadCodeElimination(List<IRInstruction> instructions)
        {
            // 收集所有被使用的寄存器
            var usedRegs = new HashSet<string>();
            foreach (var inst in instructions)
            {
                if (!string.IsNullOrEmpty(inst.Operand1)) usedRegs.Add(inst.Operand1);
                if (!string.IsNullOrEmpty(inst.Operand2)) usedRegs.Add(inst.Operand2);
                if (!string.IsNullOrEmpty(inst.Operand3)) usedRegs.Add(inst.Operand3);
            }

            // 移除目标寄存器从未被使用的指令（保留副作用指令）
            var result = new List<IRInstruction>();
            var definedRegs = new HashSet<string>();

            foreach (var inst in instructions)
            {
                bool hasSideEffect = inst.OpCode == IROpCode.StoreVar ||
                                     inst.OpCode == IROpCode.Call ||
                                     inst.OpCode == IROpCode.PrintString ||
                                     inst.OpCode == IROpCode.Return ||
                                     inst.OpCode == IROpCode.Branch ||
                                     inst.OpCode == IROpCode.Jump ||
                                     inst.OpCode == IROpCode.JumpLabel ||
                                     inst.OpCode == IROpCode.SpawnActor ||
                                     inst.OpCode == IROpCode.DestroyActor ||
                                     inst.OpCode == IROpCode.ApplyDamage ||
                                     inst.OpCode == IROpCode.Nop;

                bool isUsed = !string.IsNullOrEmpty(inst.Destination) && usedRegs.Contains(inst.Destination);

                if (!hasSideEffect && !isUsed && !string.IsNullOrEmpty(inst.Destination) &&
                    inst.OpCode != IROpCode.LoadConst)
                {
                    DeadCodeCount++;
                    continue;
                }

                result.Add(inst);
                if (!string.IsNullOrEmpty(inst.Destination))
                {
                    definedRegs.Add(inst.Destination);
                }
            }

            return result;
        }

        /// <summary>
        /// 公共子表达式消除 (CSE)。
        /// </summary>
        public List<IRInstruction> CommonSubexpressionElimination(List<IRInstruction> instructions)
        {
            var result = new List<IRInstruction>();
            var seenExpressions = new Dictionary<string, string>();

            foreach (var inst in instructions)
            {
                if (IsBinaryMathOp(inst.OpCode) || inst.OpCode == IROpCode.CmpEq || inst.OpCode == IROpCode.CmpNe)
                {
                    var key = $"{inst.OpCode}:{inst.Operand1}:{inst.Operand2}";
                    if (seenExpressions.TryGetValue(key, out var existingDest))
                    {
                        // 重用已有计算结果
                        result.Add(new IRInstruction
                        {
                            OpCode = IROpCode.Nop,
                            Destination = inst.Destination,
                            Comment = $"[CSE] {inst.Destination} = {existingDest}",
                            SourceNodeId = inst.SourceNodeId
                        });
                        seenExpressions[inst.Destination] = existingDest;
                        CSECount++;
                        continue;
                    }
                    seenExpressions[key] = inst.Destination;
                }

                result.Add(inst);
            }

            return result;
        }

        /// <summary>
        /// 强度削弱：将开销较大的运算替换为等价的较便宜的运算。
        /// <para>例如：x * 2 → x + x, x * 0.5 → x / 2, x ^ 2 → x * x</para>
        /// </summary>
        public List<IRInstruction> StrengthReduction(List<IRInstruction> instructions)
        {
            var result = new List<IRInstruction>();

            foreach (var inst in instructions)
            {
                IRInstruction? replacement = null;

                if (inst.OpCode == IROpCode.Mul && inst.Operand2 == "2")
                {
                    replacement = IRInstruction.BinaryOp(IROpCode.Add, inst.Destination, inst.Operand1, inst.Operand1, inst.SourceNodeId);
                    replacement.Comment = "[强度削弱] x*2 → x+x";
                    StrengthReductionCount++;
                }
                else if (inst.OpCode == IROpCode.Mul && inst.Operand2 == "0.5")
                {
                    replacement = IRInstruction.BinaryOp(IROpCode.Div, inst.Destination, inst.Operand1, "2", inst.SourceNodeId);
                    replacement.Comment = "[强度削弱] x*0.5 → x/2";
                    StrengthReductionCount++;
                }
                else if (inst.OpCode == IROpCode.Pow && inst.Operand2 == "2")
                {
                    replacement = IRInstruction.BinaryOp(IROpCode.Mul, inst.Destination, inst.Operand1, inst.Operand1, inst.SourceNodeId);
                    replacement.Comment = "[强度削弱] x^2 → x*x";
                    StrengthReductionCount++;
                }

                result.Add(replacement ?? inst);
            }

            return result;
        }

        // 辅助方法

        private static bool IsBinaryMathOp(IROpCode op)
        {
            return op == IROpCode.Add || op == IROpCode.Sub || op == IROpCode.Mul ||
                   op == IROpCode.Div || op == IROpCode.Mod || op == IROpCode.Pow ||
                   op == IROpCode.Min || op == IROpCode.Max;
        }

        private static string? EvaluateConstantBinary(IROpCode op, string val1, string val2)
        {
            if (!float.TryParse(val1.TrimEnd('f', 'F'), out var a)) return null;
            if (!float.TryParse(val2.TrimEnd('f', 'F'), out var b)) return null;

            float result = op switch
            {
                IROpCode.Add => a + b,
                IROpCode.Sub => a - b,
                IROpCode.Mul => a * b,
                IROpCode.Div => b != 0 ? a / b : 0,
                IROpCode.Mod => b != 0 ? a % b : 0,
                IROpCode.Pow => (float)Math.Pow(a, b),
                IROpCode.Min => Math.Min(a, b),
                IROpCode.Max => Math.Max(a, b),
                _ => float.NaN
            };

            if (float.IsNaN(result)) return null;
            return result.ToString("G6");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  CSharpCodeGenerator - C# 代码生成
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C# 代码生成器：将 IR 指令列表转换为 C# 源代码。
    /// </summary>
    public class CSharpCodeGenerator
    {
        /// <summary>缩进字符。</summary>
        private const string IndentStr = "    ";
        private int _indentLevel;

        /// <summary>
        /// 从 IR 指令列表生成 C# 源代码。
        /// </summary>
        public string Generate(string className, string namespaceName,
            List<IRInstruction> instructions,
            List<GraphVariable> variables,
            List<GraphEvent> events,
            List<GraphFunction> functions)
        {
            _indentLevel = 0;
            var sb = new StringBuilder();

            // 文件头
            sb.AppendLine("// ═══════════════════════════════════════════════════════");
            sb.AppendLine("// yama引擎 - 自动生成的蓝图 C# 脚本");
            sb.AppendLine("// 由 NodeScriptCompiler 自动生成，请勿手动修改");
            sb.AppendLine("// ═══════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Numerics;");
            sb.AppendLine("using YamaEngine.VisualScript;");
            sb.AppendLine();

            // 命名空间
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            _indentLevel++;

            // 类声明
            sb.AppendLine(Indent());
            sb.AppendLine($"public class {className} : INodeScriptScript");
            sb.AppendLine(Indent() + "{");
            _indentLevel++;

            // 变量字段
            foreach (var v in variables)
            {
                var type = NodeGraph.DataTypeToCSharpType(v.DataType);
                var access = v.AccessLevel.ToString().ToLowerInvariant();
                var defaultVal = !string.IsNullOrEmpty(v.DefaultValue) ? $" = {v.DefaultValue}" : "";
                sb.AppendLine($"{Indent()}{access} {type} {SanitizeName(v.Name)}{defaultVal};");
            }
            if (variables.Count > 0) sb.AppendLine();

            // 从 IR 生成方法
            GenerateFromIR(sb, instructions);

            sb.AppendLine();
            sb.AppendLine($"{Indent()}// INodeScriptScript 接口实现");
            sb.AppendLine($"{Indent()}public void Initialize() {{ On_BeginPlay(); }}");
            sb.AppendLine($"{Indent()}public void Tick(float deltaTime) {{ On_Tick(deltaTime); }}");

            _indentLevel--;
            sb.AppendLine(Indent() + "}");

            _indentLevel--;
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 从 IR 指令生成代码。
        /// </summary>
        private void GenerateFromIR(StringBuilder sb, List<IRInstruction> instructions)
        {
            int i = 0;
            while (i < instructions.Count)
            {
                var inst = instructions[i];

                if (inst.OpCode == IROpCode.Nop && inst.Comment != null &&
                    inst.Comment.Contains("Event:"))
                {
                    // 事件标签 → 生成方法
                    var eventName = inst.Comment.Replace("Event: ", "").Trim();
                    sb.AppendLine($"{Indent()}// ── 事件: {eventName} ──");
                    sb.AppendLine($"{Indent()}public void On_{SanitizeName(eventName)}()");
                    if (i + 1 < instructions.Count && instructions[i + 1].OpCode == IROpCode.Nop)
                    {
                        sb.AppendLine($"{Indent()}{{");
                        _indentLevel++;
                        i++;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (inst.OpCode == IROpCode.Nop && inst.Comment != null &&
                         inst.Comment.Contains("Function:"))
                {
                    var funcName = inst.Comment.Replace("Function: ", "").Trim();
                    sb.AppendLine($"{Indent()}// ── 函数: {funcName} ──");
                    sb.AppendLine($"{Indent()}public void {SanitizeName(funcName)}()");
                    if (i + 1 < instructions.Count && instructions[i + 1].OpCode != IROpCode.Return)
                    {
                        sb.AppendLine($"{Indent()}{{");
                        _indentLevel++;
                        i++;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (inst.OpCode == IROpCode.Return)
                {
                    if (_indentLevel > 1)
                    {
                        _indentLevel--;
                        sb.AppendLine($"{Indent()}}}");
                    }
                }
                else if (inst.OpCode == IROpCode.Nop)
                {
                    // 纯 NOP 跳过
                }
                else
                {
                    GenerateInstructionCode(sb, inst);
                }

                i++;
            }
        }

        /// <summary>
        /// 生成单条 IR 指令的 C# 代码。
        /// </summary>
        private void GenerateInstructionCode(StringBuilder sb, IRInstruction inst)
        {
            switch (inst.OpCode)
            {
                case IROpCode.LoadConst:
                    sb.AppendLine($"{Indent()}// var {inst.Destination} = {inst.ConstantValue};");
                    break;

                case IROpCode.LoadVar:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1};");
                    break;

                case IROpCode.StoreVar:
                    sb.AppendLine($"{Indent()}{inst.Destination} = {inst.Operand1};");
                    break;

                case IROpCode.Add:
                case IROpCode.Sub:
                case IROpCode.Mul:
                case IROpCode.Div:
                case IROpCode.Mod:
                case IROpCode.Min:
                case IROpCode.Max:
                    var opStr = inst.OpCode.ToString().ToLowerInvariant();
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1} {GetOpSymbol(inst.OpCode)} {inst.Operand2};");
                    break;

                case IROpCode.CmpEq:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} == {inst.Operand2});");
                    break;
                case IROpCode.CmpNe:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} != {inst.Operand2});");
                    break;
                case IROpCode.CmpGt:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} > {inst.Operand2});");
                    break;
                case IROpCode.CmpLt:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} < {inst.Operand2});");
                    break;

                case IROpCode.LogicAnd:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} && {inst.Operand2});");
                    break;
                case IROpCode.LogicOr:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = ({inst.Operand1} || {inst.Operand2});");
                    break;
                case IROpCode.LogicNot:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = !({inst.Operand1});");
                    break;

                case IROpCode.Branch:
                    sb.AppendLine($"{Indent()}if ({inst.Operand1})");
                    sb.AppendLine($"{Indent()}{{");
                    _indentLevel++;
                    break;

                case IROpCode.Jump:
                    // Jump 通常用于 if-else 的 else 分支末尾
                    if (_indentLevel > 1)
                    {
                        _indentLevel--;
                        sb.AppendLine($"{Indent()}}}");
                    }
                    break;

                case IROpCode.JumpLabel:
                    // 标签在 C# 中不需要显式表示
                    break;

                case IROpCode.Call:
                    var args = inst.Label ?? "";
                    if (string.IsNullOrEmpty(inst.Destination))
                        sb.AppendLine($"{Indent()}{inst.Operand1}({args});");
                    else
                        sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1}({args});");
                    break;

                case IROpCode.PrintString:
                    sb.AppendLine($"{Indent()}System.Console.WriteLine({inst.Operand1});");
                    break;

                case IROpCode.Slerp:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = Quaternion.Slerp({inst.Operand1}, {inst.Operand2}, {inst.Operand3});");
                    break;

                case IROpCode.Vec3Add:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1} + {inst.Operand2};");
                    break;
                case IROpCode.Vec3Sub:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1} - {inst.Operand2};");
                    break;
                case IROpCode.Vec3Scale:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1} * {inst.Operand2};");
                    break;
                case IROpCode.Vec3Dot:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = Vector3.Dot({inst.Operand1}, {inst.Operand2});");
                    break;
                case IROpCode.Vec3Cross:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = Vector3.Cross({inst.Operand1}, {inst.Operand2});");
                    break;
                case IROpCode.Vec3Normalize:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = Vector3.Normalize({inst.Operand1});");
                    break;
                case IROpCode.Vec3Length:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = {inst.Operand1}.Length();");
                    break;
                case IROpCode.Vec3Make:
                    sb.AppendLine($"{Indent()}var {inst.Destination} = new Vector3({inst.Operand1}, {inst.Operand2}, {inst.Operand3});");
                    break;

                default:
                    if (!string.IsNullOrEmpty(inst.Comment))
                        sb.AppendLine($"{Indent()}// {inst.Comment}");
                    sb.AppendLine($"{Indent()}// TODO: {inst.OpCode}");
                    break;
            }
        }

        private string Indent() => string.Concat(Enumerable.Repeat(IndentStr, _indentLevel));

        private static string GetOpSymbol(IROpCode op)
        {
            return op switch
            {
                IROpCode.Add => "+",
                IROpCode.Sub => "-",
                IROpCode.Mul => "*",
                IROpCode.Div => "/",
                IROpCode.Mod => "%",
                IROpCode.Min => " < ? ",
                IROpCode.Max => " > ? ",
                _ => op.ToString()
            };
        }

        private static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.Length > 0 ? sb.ToString() : "_unnamed";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeScriptCompiler - 编译管线
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编译诊断消息。
    /// </summary>
    [Serializable]
    public class CompileMessage
    {
        /// <summary>消息严重程度。</summary>
        public ValidationSeverity Severity { get; set; }

        /// <summary>关联的节点 ID。</summary>
        public Guid? NodeId { get; set; }

        /// <summary>关联的 IR 指令索引。</summary>
        public int? InstructionIndex { get; set; }

        /// <summary>消息描述。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>修复建议。</summary>
        public string? Suggestion { get; set; }

        /// <summary>源代码行号（代码生成后）。</summary>
        public int? LineNumber { get; set; }

        public override string ToString() => $"[{Severity}] {Message}";
    }

    /// <summary>
    /// 编译结果。
    /// </summary>
    [Serializable]
    public class CompileResult
    {
        /// <summary>编译是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>生成的 C# 源代码（或 IR 文本）。</summary>
        public string OutputCode { get; set; } = string.Empty;

        /// <summary>生成的 IR 指令列表。</summary>
        public List<IRInstruction> Instructions { get; set; } = new List<IRInstruction>();

        /// <summary>编译消息（错误/警告/信息）。</summary>
        public List<CompileMessage> Messages { get; set; } = new List<CompileMessage>();

        /// <summary>编译耗时（毫秒）。</summary>
        public long ElapsedMs { get; set; }

        /// <summary>优化统计。</summary>
        public string OptimizationStats { get; set; } = string.Empty;
    }

    /// <summary>
    /// 蓝图编译器：完整的编译管线。
    /// <para>流程：节点图 → 验证 → IR生成 → IR优化 → C#代码生成</para>
    /// </summary>
    public class NodeScriptCompiler
    {
        /// <summary>IR 生成器。</summary>
        private readonly IRGenerator _irGenerator = new IRGenerator();

        /// <summary>IR 优化器。</summary>
        private readonly IROptimizer _irOptimizer = new IROptimizer();

        /// <summary>C# 代码生成器。</summary>
        private readonly CSharpCodeGenerator _codeGenerator = new CSharpCodeGenerator();

        /// <summary>是否启用优化。</summary>
        public bool EnableOptimization { get; set; } = true;

        /// <summary>目标命名空间。</summary>
        public string TargetNamespace { get; set; } = "YamaEngine.GeneratedScripts";

        /// <summary>增量编译缓存（节点 ID → 最后编译版本）。</summary>
        private Dictionary<Guid, int> _nodeVersions = new Dictionary<Guid, int>();

        /// <summary>需要重新编译的脏节点 ID 集合。</summary>
        private HashSet<Guid> _dirtyNodes = new HashSet<Guid>();

        /// <summary>
        /// 完整编译管线。
        /// </summary>
        public CompileResult Compile(NodeGraph graph, CompileOutputFormat format = CompileOutputFormat.CSharp)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new CompileResult();

            // 阶段 1：验证
            var validationMessages = graph.Validate();
            result.Messages.AddRange(validationMessages.Select(m => new CompileMessage
            {
                Severity = m.Severity,
                NodeId = m.NodeId,
                Message = m.Message,
                Suggestion = m.Suggestion
            }));

            var errors = result.Messages.Where(m => m.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                result.Success = false;
                result.OutputCode = $"// 编译失败：{errors.Count} 个错误\n" +
                    string.Join("\n", errors.Select(e => $"// ERROR: {e.Message}"));
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            // 阶段 2：IR 生成
            var ir = _irGenerator.Generate(graph);
            result.Instructions = ir;

            result.Messages.AddRange(_irGenerator.Messages.Select(m => new CompileMessage
            {
                Severity = m.Severity,
                NodeId = m.NodeId,
                Message = m.Message,
                Suggestion = m.Suggestion
            }));

            // 阶段 3：IR 优化
            if (EnableOptimization)
            {
                ir = _irOptimizer.Optimize(ir);
                result.OptimizationStats =
                    $"常量折叠: {_irOptimizer.ConstantFoldCount}, " +
                    $"死代码消除: {_irOptimizer.DeadCodeCount}, " +
                    $"公共子表达式消除: {_irOptimizer.CSECount}, " +
                    $"强度削弱: {_irOptimizer.StrengthReductionCount}";
            }

            // 阶段 4：代码生成
            switch (format)
            {
                case CompileOutputFormat.CSharp:
                    var className = SanitizeName(graph.Name) + "NodeScript";
                    result.OutputCode = _codeGenerator.Generate(
                        className, TargetNamespace, ir,
                        graph.Variables, graph.Events, graph.Functions);
                    break;

                case CompileOutputFormat.IntermediateRepresentation:
                    var irSb = new System.Text.StringBuilder();
                    irSb.AppendLine($"; IR for graph: {graph.Name}");
                    irSb.AppendLine($"; Nodes: {graph.Nodes.Count}, Connections: {graph.Connections.Count}");
                    irSb.AppendLine();
                    foreach (var inst in ir)
                    {
                        irSb.AppendLine(inst.ToString());
                    }
                    result.OutputCode = irSb.ToString();
                    break;

                default:
                    result.OutputCode = $"// 不支持的输出格式: {format}";
                    break;
            }

            // 更新节点版本缓存
            foreach (var node in graph.Nodes)
            {
                _nodeVersions[node.Id] = node.ModifiedAt.GetHashCode();
            }
            _dirtyNodes.Clear();

            result.Success = true;
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;

            return result;
        }

        /// <summary>
        /// 标记节点为脏（需要重新编译）。
        /// </summary>
        public void MarkDirty(Guid nodeId)
        {
            _dirtyNodes.Add(nodeId);
        }

        /// <summary>
        /// 增量编译：只重新编译变更的函数/事件。
        /// </summary>
        public CompileResult IncrementalCompile(NodeGraph graph)
        {
            // 检查哪些节点发生了变化
            foreach (var node in graph.Nodes)
            {
                if (_nodeVersions.TryGetValue(node.Id, out var version))
                {
                    if (version != node.ModifiedAt.GetHashCode())
                    {
                        MarkDirty(node.Id);
                    }
                }
                else
                {
                    MarkDirty(node.Id);
                }
            }

            if (_dirtyNodes.Count == 0)
            {
                return new CompileResult
                {
                    Success = true,
                    OutputCode = "// 增量编译：无变更，跳过",
                    ElapsedMs = 0
                };
            }

            // 执行完整编译（简化版增量编译，实际可以只编译变更的函数）
            return Compile(graph);
        }

        /// <summary>
        /// 仅生成 IR（不生成最终代码）。
        /// </summary>
        public List<IRInstruction> GenerateIR(NodeGraph graph)
        {
            return _irGenerator.Generate(graph);
        }

        /// <summary>
        /// 仅优化 IR。
        /// </summary>
        public List<IRInstruction> OptimizeIR(List<IRInstruction> ir)
        {
            return _irOptimizer.Optimize(ir);
        }

        private static string SanitizeName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.Length > 0 ? sb.ToString() : "_unnamed";
        }
    }

    /// <summary>
    /// 蓝图脚本接口：所有编译生成的蓝图脚本都实现此接口。
    /// </summary>
    public interface INodeScriptScript
    {
        /// <summary>初始化（BeginPlay 时调用）。</summary>
        void Initialize();

        /// <summary>每帧更新。</summary>
        /// <param name="deltaTime">帧间隔时间。</param>
        void Tick(float deltaTime);
    }
}
