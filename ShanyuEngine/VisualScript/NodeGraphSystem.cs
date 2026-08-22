using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShanyuEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 · 节点图核心系统 (Node Graph Core System)
    //  ────────────────────────────────────────────────────────────────────────────
    //  实现可视化脚本编辑器的核心数据模型：
    //    · GraphPinDirection  — 引脚方向枚举
    //    · GraphPin           — 引脚（输入/输出，支持数据类型、默认值、连接列表、数组）
    //    · GraphConnection    — 连线（源节点/引脚 → 目标节点/引脚）
    //    · GraphNode          — 节点基类（ID/类型/位置/大小/标题/颜色/引脚/注释/可编译）
    //    · GraphVariable      — 图变量（名称/类型/默认值/访问权限/可序列化）
    //    · GraphEvent         — 事件节点
    //    · GraphFunction      — 函数节点
    //    · NodeGraph          — 节点图（节点/连接/变量/事件/函数/编译/执行/序列化/验证）
    //  作者：山雨引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  枚举与基础类型
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点图引脚方向。
    /// </summary>
    public enum GraphPinDirection
    {
        /// <summary>输入引脚（节点左侧）。</summary>
        Input,
        /// <summary>输出引脚（节点右侧）。</summary>
        Output
    }

    /// <summary>
    /// 节点图引脚支持的数据类型。
    /// </summary>
    public enum GraphPinDataType
    {
        /// <summary>执行流引脚。</summary>
        Exec,
        /// <summary>布尔值 (bool)。</summary>
        Bool,
        /// <summary>8位无符号整数 (byte)。</summary>
        Byte,
        /// <summary>32位有符号整数 (int)。</summary>
        Int,
        /// <summary>单精度浮点 (float)。</summary>
        Float,
        /// <summary>双精度浮点 (double)。</summary>
        Double,
        /// <summary>字符串 (string)。</summary>
        String,
        /// <summary>名称标识 (Name)。</summary>
        Name,
        /// <summary>枚举类型。</summary>
        Enum,
        /// <summary>二维向量 (Vector2)。</summary>
        Vector2,
        /// <summary>三维向量 (Vector3)。</summary>
        Vector3,
        /// <summary>四维向量 (Vector4)。</summary>
        Vector4,
        /// <summary>旋转体 (Rotator - Euler angles)。</summary>
        Rotator,
        /// <summary>四元数 (Quaternion)。</summary>
        Quaternion,
        /// <summary>变换 (Transform - Location/Rotation/Scale)。</summary>
        Transform,
        /// <summary>颜色 (Color)。</summary>
        Color,
        /// <summary>任意对象引用 (object)。</summary>
        Object,
        /// <summary>数组类型。</summary>
        Array,
        /// <summary>映射/字典类型。</summary>
        Map,
        /// <summary>集合类型。</summary>
        Set,
        /// <summary>结构体类型。</summary>
        Struct,
        /// <summary>通配符 / 任意类型。</summary>
        Wildcard
    }

    /// <summary>
    /// 变量访问权限。
    /// </summary>
    public enum VariableAccessLevel
    {
        /// <summary>私有。</summary>
        Private,
        /// <summary>受保护。</summary>
        Protected,
        /// <summary>公共。</summary>
        Public,
        /// <summary>内部。</summary>
        Internal
    }

    /// <summary>
    /// 函数访问修饰符。
    /// </summary>
    public enum FunctionAccessModifier
    {
        /// <summary>私有。</summary>
        Private,
        /// <summary>受保护。</summary>
        Protected,
        /// <summary>公共。</summary>
        Public,
        /// <summary>内部。</summary>
        Internal,
        /// <summary>公开可调用 (蓝图可调用)。</summary>
        NodeScriptCallable
    }

    /// <summary>
    /// 引脚显示类型（用于编辑器可视化区分）。
    /// </summary>
    public enum PinDisplayType
    {
        /// <summary>默认圆形引脚。</summary>
        Default,
        /// <summary>仅连接时显示。</summary>
        ConnectedOnly,
        /// <summary>始终显示为列表。</summary>
        AlwaysAsList,
        /// <summary>仅值显示。</summary>
        ValueOnly,
        /// <summary>引用显示。</summary>
        Reference
    }

    /// <summary>
    /// 蓝图编译输出格式。
    /// </summary>
    public enum CompileOutputFormat
    {
        /// <summary>C# 源代码。</summary>
        CSharp,
        /// <summary>HLSL 着色器代码。</summary>
        HLSL,
        /// <summary>中间表示 (IR)。</summary>
        IntermediateRepresentation,
        /// <summary>字节码。</summary>
        Bytecode
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphPin - 引脚
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点引脚：代表节点的输入或输出端口。
    /// <para>支持数据类型、默认值、连接列表、显示类型、可选标记和数组标记。</para>
    /// </summary>
    [Serializable]
    public class GraphPin
    {
        /// <summary>引脚唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>引脚所属节点的 ID。</summary>
        public Guid OwnerNodeId { get; set; }

        /// <summary>引脚方向（输入或输出）。</summary>
        public GraphPinDirection Direction { get; set; }

        /// <summary>引脚数据类型。</summary>
        public GraphPinDataType DataType { get; set; } = GraphPinDataType.Exec;

        /// <summary>引脚显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚默认值（JSON 字符串形式）。</summary>
        public string? DefaultValue { get; set; }

        /// <summary>与该引脚相连的所有连接 ID。</summary>
        public List<Guid> ConnectionIds { get; set; } = new List<Guid>();

        /// <summary>引脚显示类型。</summary>
        public PinDisplayType DisplayType { get; set; } = PinDisplayType.Default;

        /// <summary>该引脚是否可选（未连接时不报错）。</summary>
        public bool IsOptional { get; set; }

        /// <summary>该引脚是否为数组类型。</summary>
        public bool IsArray { get; set; }

        /// <summary>数组元素类型（当 IsArray 为 true 时有效）。</summary>
        public GraphPinDataType ArrayElementType { get; set; }

        /// <summary>引脚在节点上的显示顺序索引。</summary>
        public int OrderIndex { get; set; }

        /// <summary>引脚在节点上的 Y 偏移量（用于渲染定位）。</summary>
        public float YOffset { get; set; }

        /// <summary>自定义子类型名称（用于 Struct / Enum / Object 等）。</summary>
        public string? SubTypeName { get; set; }

        /// <summary>该引脚是否已连接。</summary>
        [JsonIgnore]
        public bool IsConnected => ConnectionIds.Count > 0;

        /// <summary>该引脚是否有默认值。</summary>
        [JsonIgnore]
        public bool HasDefaultValue => !string.IsNullOrEmpty(DefaultValue);

        /// <summary>
        /// 创建一个新的输入引脚。
        /// </summary>
        public static GraphPin CreateInput(string name, GraphPinDataType dataType, string? defaultValue = null, int orderIndex = 0)
        {
            return new GraphPin
            {
                Name = name,
                Direction = GraphPinDirection.Input,
                DataType = dataType,
                DefaultValue = defaultValue,
                OrderIndex = orderIndex
            };
        }

        /// <summary>
        /// 创建一个新的输出引脚。
        /// </summary>
        public static GraphPin CreateOutput(string name, GraphPinDataType dataType, int orderIndex = 0)
        {
            return new GraphPin
            {
                Name = name,
                Direction = GraphPinDirection.Output,
                DataType = dataType,
                OrderIndex = orderIndex
            };
        }

        /// <summary>
        /// 创建一个执行流输入引脚。
        /// </summary>
        public static GraphPin CreateExecInput(string name = "Exec", int orderIndex = 0)
        {
            return CreateInput(name, GraphPinDataType.Exec, null, orderIndex);
        }

        /// <summary>
        /// 创建一个执行流输出引脚。
        /// </summary>
        public static GraphPin CreateExecOutput(string name = "Then", int orderIndex = 0)
        {
            return CreateOutput(name, GraphPinDataType.Exec, orderIndex);
        }

        /// <summary>
        /// 序列化引脚为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化引脚。
        /// </summary>
        public static GraphPin? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphPin>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphConnection - 连线
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点间连线：表示一个源节点的输出引脚到一个目标节点的输入引脚的连接。
    /// </summary>
    [Serializable]
    public class GraphConnection
    {
        /// <summary>连接唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>源节点 ID。</summary>
        public Guid SourceNodeId { get; set; }

        /// <summary>源引脚 ID。</summary>
        public Guid SourcePinId { get; set; }

        /// <summary>目标节点 ID。</summary>
        public Guid TargetNodeId { get; set; }

        /// <summary>目标引脚 ID。</summary>
        public Guid TargetPinId { get; set; }

        /// <summary>连线颜色（可按类型自动分配或自定义）。</summary>
        public string ColorHex { get; set; } = "#FFFFFF";

        /// <summary>连线是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 创建一个新的连线。
        /// </summary>
        public static GraphConnection Create(Guid sourceNode, Guid sourcePin, Guid targetNode, Guid targetPin, string colorHex = "#FFFFFF")
        {
            return new GraphConnection
            {
                SourceNodeId = sourceNode,
                SourcePinId = sourcePin,
                TargetNodeId = targetNode,
                TargetPinId = targetPin,
                ColorHex = colorHex
            };
        }

        /// <summary>
        /// 序列化连线为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化连线。
        /// </summary>
        public static GraphConnection? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphConnection>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphNode - 节点基类
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点图节点基类：可视化脚本中的基本执行和数据单元。
    /// <para>每个节点拥有 ID、类型、位置、大小、标题、颜色、输入/输出引脚、注释和可编译标记。</para>
    /// </summary>
    [Serializable]
    public class GraphNode
    {
        /// <summary>节点唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>节点类型名称（如 "Add"/"Branch"/"EventBeginPlay" 等）。</summary>
        public string NodeType { get; set; } = "Node";

        /// <summary>节点标题。</summary>
        public string Title { get; set; } = "Node";

        /// <summary>节点子标题（可选，显示额外信息）。</summary>
        public string? Subtitle { get; set; }

        /// <summary>节点分类（用于编辑器分组和颜色）。</summary>
        public string Category { get; set; } = "Default";

        /// <summary>节点在画布上的位置 (X, Y)。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小 (Width, Height)。</summary>
        public Vector2 Size { get; set; } = new Vector2(200, 100);

        /// <summary>节点颜色（十六进制字符串）。</summary>
        public string ColorHex { get; set; } = "#808080";

        /// <summary>节点是否可编译（注释节点等不可编译）。</summary>
        public bool IsCompilable { get; set; } = true;

        /// <summary>节点是否已启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>节点是否被折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>节点是否为高级节点（默认在搜索中隐藏）。</summary>
        public bool IsAdvanced { get; set; }

        /// <summary>节点注释。</summary>
        public string? Comment { get; set; }

        /// <summary>节点输入引脚列表。</summary>
        public List<GraphPin> InputPins { get; set; } = new List<GraphPin>();

        /// <summary>节点输出引脚列表。</summary>
        public List<GraphPin> OutputPins { get; set; } = new List<GraphPin>();

        /// <summary>节点自定义数据（扩展用，JSON 存储）。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>节点创建时间戳。</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>节点最后修改时间戳。</summary>
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>获取所有引脚（输入 + 输出）。</summary>
        [JsonIgnore]
        public IEnumerable<GraphPin> AllPins => InputPins.Concat(OutputPins);

        /// <summary>通过 ID 获取引脚。</summary>
        public GraphPin? GetPin(Guid pinId)
        {
            return InputPins.FirstOrDefault(p => p.Id == pinId)
                ?? OutputPins.FirstOrDefault(p => p.Id == pinId);
        }

        /// <summary>通过名称获取输入引脚。</summary>
        public GraphPin? GetInputPin(string name)
        {
            return InputPins.FirstOrDefault(p => p.Name == name);
        }

        /// <summary>通过名称获取输出引脚。</summary>
        public GraphPin? GetOutputPin(string name)
        {
            return OutputPins.FirstOrDefault(p => p.Name == name);
        }

        /// <summary>
        /// 添加输入引脚。
        /// </summary>
        public GraphPin AddInputPin(string name, GraphPinDataType dataType, string? defaultValue = null, bool isOptional = false)
        {
            var pin = GraphPin.CreateInput(name, dataType, defaultValue, InputPins.Count);
            pin.OwnerNodeId = Id;
            pin.IsOptional = isOptional;
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>
        /// 添加输出引脚。
        /// </summary>
        public GraphPin AddOutputPin(string name, GraphPinDataType dataType)
        {
            var pin = GraphPin.CreateOutput(name, dataType, OutputPins.Count);
            pin.OwnerNodeId = Id;
            OutputPins.Add(pin);
            return pin;
        }

        /// <summary>
        /// 添加执行流输入引脚。
        /// </summary>
        public GraphPin AddExecInput(string name = "Exec")
        {
            return AddInputPin(name, GraphPinDataType.Exec);
        }

        /// <summary>
        /// 添加执行流输出引脚。
        /// </summary>
        public GraphPin AddExecOutput(string name = "Then")
        {
            return AddOutputPin(name, GraphPinDataType.Exec);
        }

        /// <summary>
        /// 移除引脚。
        /// </summary>
        public bool RemovePin(Guid pinId)
        {
            return InputPins.RemoveAll(p => p.Id == pinId) > 0
                || OutputPins.RemoveAll(p => p.Id == pinId) > 0;
        }

        /// <summary>
        /// 节点执行方法（子类重写以实现具体逻辑）。
        /// </summary>
        public virtual void Execute(Dictionary<Guid, object?> context)
        {
            // 默认空实现
        }

        /// <summary>
        /// 序列化节点为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化节点。
        /// </summary>
        public static GraphNode? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphNode>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphVariable - 图变量
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 图变量：节点图中定义的变量，可在节点之间共享和读写。
    /// </summary>
    [Serializable]
    public class GraphVariable
    {
        /// <summary>变量唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>变量名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>变量数据类型。</summary>
        public GraphPinDataType DataType { get; set; } = GraphPinDataType.Float;

        /// <summary>变量默认值（JSON 字符串）。</summary>
        public string? DefaultValue { get; set; }

        /// <summary>变量访问权限。</summary>
        public VariableAccessLevel AccessLevel { get; set; } = VariableAccessLevel.Public;

        /// <summary>变量是否可序列化（持久化）。</summary>
        public bool IsSerializable { get; set; } = true;

        /// <summary>变量是否为数组。</summary>
        public bool IsArray { get; set; }

        /// <summary>数组元素类型（当 IsArray 为 true）。</summary>
        public GraphPinDataType ArrayElementType { get; set; }

        /// <summary>变量实例 ID（用于实例级变量覆盖）。</summary>
        public Guid InstanceId { get; set; }

        /// <summary>变量注释。</summary>
        public string? Comment { get; set; }

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 序列化变量为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化变量。
        /// </summary>
        public static GraphVariable? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphVariable>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphEvent - 事件节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 事件节点：定义蓝图可响应的事件（如 BeginPlay、Tick、碰撞事件等）。
    /// <para>事件节点是蓝图执行的入口点，只有输出引脚，没有输入引脚。</para>
    /// </summary>
    [Serializable]
    public class GraphEvent
    {
        /// <summary>事件唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>事件名称（如 "BeginPlay", "Tick", "OnHit" 等）。</summary>
        public string EventName { get; set; } = "Event";

        /// <summary>事件分类（如 "Game"/"Actor"/"Input"/"Collision"/"Custom"）。</summary>
        public string Category { get; set; } = "Custom";

        /// <summary>事件描述。</summary>
        public string? Description { get; set; }

        /// <summary>事件的输出引脚（事件参数）。</summary>
        public List<GraphPin> OutputPins { get; set; } = new List<GraphPin>();

        /// <summary>事件关联的节点 ID（用于图内映射）。</summary>
        public Guid NodeId { get; set; }

        /// <summary>事件节点在画布上的位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>事件是否为蓝图自定义事件。</summary>
        public bool IsCustomEvent { get; set; }

        /// <summary>事件是否在编辑器中折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>事件颜色。</summary>
        public string ColorHex { get; set; } = "#CC0000";

        /// <summary>事件节点注释。</summary>
        public string? Comment { get; set; }

        /// <summary>
        /// 创建标准事件节点。
        /// </summary>
        public static GraphEvent Create(string eventName, string category = "Custom", Vector2 position = default)
        {
            var evt = new GraphEvent
            {
                EventName = eventName,
                Category = category,
                Position = position,
                ColorHex = category switch
                {
                    "Game" => "#CC0000",
                    "Actor" => "#990000",
                    "Input" => "#0066CC",
                    "Collision" => "#CC6600",
                    _ => "#CC0000"
                }
            };

            // 添加默认的执行流输出引脚
            evt.OutputPins.Add(GraphPin.CreateExecOutput("Exec", 0));

            return evt;
        }

        /// <summary>
        /// 添加事件输出参数引脚。
        /// </summary>
        public GraphPin AddOutputPin(string name, GraphPinDataType dataType, string? defaultValue = null)
        {
            var pin = GraphPin.CreateOutput(name, dataType, evt.OutputPins.Count);
            evt.OutputPins.Add(pin);
            return pin;
        }

        private GraphEvent evt => this;

        /// <summary>
        /// 序列化事件为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化事件。
        /// </summary>
        public static GraphEvent? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphEvent>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GraphFunction - 函数节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 函数节点：定义蓝图中可复用的函数。
    /// <para>函数有输入参数和输出参数（返回值），支持访问修饰符。</para>
    /// </summary>
    [Serializable]
    public class GraphFunction
    {
        /// <summary>函数唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>函数名称。</summary>
        public string FunctionName { get; set; } = "NewFunction";

        /// <summary>函数显示标题。</summary>
        public string DisplayName { get; set; } = "New Function";

        /// <summary>函数描述。</summary>
        public string? Description { get; set; }

        /// <summary>函数访问修饰符。</summary>
        public FunctionAccessModifier AccessModifier { get; set; } = FunctionAccessModifier.Public;

        /// <summary>函数是否为纯函数（无副作用，可内联）。</summary>
        public bool IsPure { get; set; }

        /// <summary>函数返回类型（输出引脚的第一个数据引脚）。</summary>
        public GraphPinDataType? ReturnType { get; set; }

        /// <summary>函数输入参数引脚。</summary>
        public List<GraphPin> InputPins { get; set; } = new List<GraphPin>();

        /// <summary>函数输出引脚（含返回值）。</summary>
        public List<GraphPin> OutputPins { get; set; } = new List<GraphPin>();

        /// <summary>函数关联的入口节点 ID。</summary>
        public Guid EntryNodeId { get; set; }

        /// <summary>函数内部节点图（子图）。</summary>
        public List<GraphNode> InnerNodes { get; set; } = new List<GraphNode>();

        /// <summary>函数内部连接。</summary>
        public List<GraphConnection> InnerConnections { get; set; } = new List<GraphConnection>();

        /// <summary>函数节点在画布上的位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>函数颜色。</summary>
        public string ColorHex { get; set; } = "#0066CC";

        /// <summary>函数是否在编辑器中折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>函数注释。</summary>
        public string? Comment { get; set; }

        /// <summary>
        /// 创建一个新函数。
        /// </summary>
        public static GraphFunction Create(string functionName, FunctionAccessModifier access = FunctionAccessModifier.Public, Vector2 position = default)
        {
            var func = new GraphFunction
            {
                FunctionName = functionName,
                DisplayName = functionName,
                AccessModifier = access,
                Position = position,
                ColorHex = access switch
                {
                    FunctionAccessModifier.Public => "#0066CC",
                    FunctionAccessModifier.Private => "#000066",
                    FunctionAccessModifier.Protected => "#333399",
                    FunctionAccessModifier.NodeScriptCallable => "#0099FF",
                    _ => "#0066CC"
                }
            };

            // 添加默认执行流输入和输出
            func.InputPins.Add(GraphPin.CreateExecInput("Exec", 0));
            func.OutputPins.Add(GraphPin.CreateExecOutput("Return", 0));

            return func;
        }

        /// <summary>
        /// 添加函数输入参数。
        /// </summary>
        public GraphPin AddInputPin(string name, GraphPinDataType dataType, string? defaultValue = null, bool isOptional = false)
        {
            var pin = GraphPin.CreateInput(name, dataType, defaultValue, InputPins.Count);
            pin.IsOptional = isOptional;
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>
        /// 添加函数输出参数。
        /// </summary>
        public GraphPin AddOutputPin(string name, GraphPinDataType dataType)
        {
            var pin = GraphPin.CreateOutput(name, dataType, OutputPins.Count);
            OutputPins.Add(pin);
            return pin;
        }

        /// <summary>
        /// 序列化函数为 JSON。
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// 从 JSON 反序列化函数。
        /// </summary>
        public static GraphFunction? FromJson(string json)
        {
            return JsonSerializer.Deserialize<GraphFunction>(json);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  图验证结果
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证消息严重程度。
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>信息。</summary>
        Info,
        /// <summary>警告。</summary>
        Warning,
        /// <summary>错误。</summary>
        Error
    }

    /// <summary>
    /// 图验证消息。
    /// </summary>
    [Serializable]
    public class GraphValidationMessage
    {
        /// <summary>严重程度。</summary>
        public ValidationSeverity Severity { get; set; }

        /// <summary>关联的节点 ID（可选）。</summary>
        public Guid? NodeId { get; set; }

        /// <summary>关联的引脚 ID（可选）。</summary>
        public Guid? PinId { get; set; }

        /// <summary>关联的连接 ID（可选）。</summary>
        public Guid? ConnectionId { get; set; }

        /// <summary>验证消息描述。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>修复建议（可选）。</summary>
        public string? Suggestion { get; set; }

        public override string ToString() => $"[{Severity}] {Message}";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeGraph - 节点图
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点图：可视化脚本的顶层容器。
    /// <para>管理节点、连接、变量、事件和函数，支持执行、编译、序列化和验证。</para>
    /// </summary>
    [Serializable]
    public class NodeGraph
    {
        /// <summary>图唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>图名称。</summary>
        public string Name { get; set; } = "UntitledGraph";

        /// <summary>图的显示标题。</summary>
        public string DisplayName { get; set; } = "Untitled Graph";

        /// <summary>图描述。</summary>
        public string? Description { get; set; }

        /// <summary>图类型（如 "EventGraph"/"ConstructionScript"/"AnimGraph"）。</summary>
        public string GraphType { get; set; } = "EventGraph";

        /// <summary>所属蓝图/资源路径。</summary>
        public string? OwnerAssetPath { get; set; }

        /// <summary>图中的所有节点。</summary>
        public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();

        /// <summary>图中的所有连接。</summary>
        public List<GraphConnection> Connections { get; set; } = new List<GraphConnection>();

        /// <summary>图中定义的变量。</summary>
        public List<GraphVariable> Variables { get; set; } = new List<GraphVariable>();

        /// <summary>图中定义的事件。</summary>
        public List<GraphEvent> Events { get; set; } = new List<GraphEvent>();

        /// <summary>图中定义的函数。</summary>
        public List<GraphFunction> Functions { get; set; } = new List<GraphFunction>();

        /// <summary>图是否为只读。</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>图的版本号（用于增量编译和版本管理）。</summary>
        public int Version { get; set; }

        /// <summary>图的自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        // ───── 节点操作 ─────

        /// <summary>添加节点到图中。</summary>
        public GraphNode AddNode(GraphNode node)
        {
            Nodes.Add(node);
            return node;
        }

        /// <summary>从图中移除节点及其所有连接。</summary>
        public bool RemoveNode(Guid nodeId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;

            // 移除与该节点相关的所有连接
            RemoveNodeConnections(nodeId);

            Nodes.Remove(node);
            return true;
        }

        /// <summary>通过 ID 获取节点。</summary>
        public GraphNode? GetNode(Guid nodeId)
        {
            return Nodes.FirstOrDefault(n => n.Id == nodeId);
        }

        /// <summary>通过类型名称查找节点。</summary>
        public List<GraphNode> FindNodesByType(string nodeType)
        {
            return Nodes.Where(n => n.NodeType == nodeType).ToList();
        }

        /// <summary>通过标题搜索节点（模糊匹配）。</summary>
        public List<GraphNode> SearchNodes(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return Nodes.ToList();
            var lower = keyword.ToLowerInvariant();
            return Nodes.Where(n =>
                n.Title.ToLowerInvariant().Contains(lower) ||
                n.NodeType.ToLowerInvariant().Contains(lower) ||
                (n.Category?.ToLowerInvariant().Contains(lower) ?? false)
            ).ToList();
        }

        // ───── 连接操作 ─────

        /// <summary>创建连接。</summary>
        /// <returns>新创建的连接，若连接无效则返回 null。</returns>
        public GraphConnection? Connect(Guid sourceNode, Guid sourcePin, Guid targetNode, Guid targetPin)
        {
            var srcNode = GetNode(sourceNode);
            var tgtNode = GetNode(targetNode);
            if (srcNode == null || tgtNode == null) return null;

            var srcPin = srcNode.GetPin(sourcePin);
            var tgtPin = tgtNode.GetPin(targetPin);
            if (srcPin == null || tgtPin == null) return null;

            // 方向检查：源必须为输出，目标必须为输入
            if (srcPin.Direction != GraphPinDirection.Output || tgtPin.Direction != GraphPinDirection.Input)
                return null;

            // 不能连接自身
            if (sourceNode == targetNode) return null;

            var connection = GraphConnection.Create(sourceNode, sourcePin, targetNode, targetPin,
                GetDataTypeColorHex(srcPin.DataType));

            Connections.Add(connection);

            // 更新引脚连接列表
            srcPin.ConnectionIds.Add(connection.Id);
            tgtPin.ConnectionIds.Add(connection.Id);

            return connection;
        }

        /// <summary>断开连接。</summary>
        public bool Disconnect(Guid connectionId)
        {
            var conn = Connections.FirstOrDefault(c => c.Id == connectionId);
            if (conn == null) return false;

            var srcNode = GetNode(conn.SourceNodeId);
            var tgtNode = GetNode(conn.TargetNodeId);

            srcNode?.GetPin(conn.SourcePinId)?.ConnectionIds.Remove(connectionId);
            tgtNode?.GetPin(conn.TargetPinId)?.ConnectionIds.Remove(connectionId);

            Connections.Remove(conn);
            return true;
        }

        /// <summary>移除与指定节点相关的所有连接。</summary>
        public void RemoveNodeConnections(Guid nodeId)
        {
            var related = Connections.Where(c => c.SourceNodeId == nodeId || c.TargetNodeId == nodeId).ToList();
            foreach (var conn in related)
            {
                Disconnect(conn.Id);
            }
        }

        /// <summary>获取连接。</summary>
        public GraphConnection? GetConnection(Guid connectionId)
        {
            return Connections.FirstOrDefault(c => c.Id == connectionId);
        }

        /// <summary>获取连接到指定引脚的所有连接。</summary>
        public List<GraphConnection> GetConnectionsForPin(Guid pinId)
        {
            return Connections.Where(c => c.SourcePinId == pinId || c.TargetPinId == pinId).ToList();
        }

        // ───── 变量操作 ─────

        /// <summary>添加变量。</summary>
        public GraphVariable AddVariable(GraphVariable variable)
        {
            Variables.Add(variable);
            return variable;
        }

        /// <summary>移除变量。</summary>
        public bool RemoveVariable(Guid variableId)
        {
            return Variables.RemoveAll(v => v.Id == variableId) > 0;
        }

        /// <summary>通过名称查找变量。</summary>
        public GraphVariable? GetVariable(string name)
        {
            return Variables.FirstOrDefault(v => v.Name == name);
        }

        // ───── 事件操作 ─────

        /// <summary>添加事件。</summary>
        public GraphEvent AddEvent(GraphEvent evt)
        {
            Events.Add(evt);
            return evt;
        }

        /// <summary>移除事件。</summary>
        public bool RemoveEvent(Guid eventId)
        {
            return Events.RemoveAll(e => e.Id == eventId) > 0;
        }

        /// <summary>通过名称查找事件。</summary>
        public GraphEvent? GetEvent(string eventName)
        {
            return Events.FirstOrDefault(e => e.EventName == eventName);
        }

        // ───── 函数操作 ─────

        /// <summary>添加函数。</summary>
        public GraphFunction AddFunction(GraphFunction func)
        {
            Functions.Add(func);
            return func;
        }

        /// <summary>移除函数。</summary>
        public bool RemoveFunction(Guid functionId)
        {
            return Functions.RemoveAll(f => f.Id == functionId) > 0;
        }

        /// <summary>通过名称查找函数。</summary>
        public GraphFunction? GetFunction(string functionName)
        {
            return Functions.FirstOrDefault(f => f.FunctionName == functionName);
        }

        // ───── 图执行（拓扑排序 + 按序执行） ─────

        /// <summary>
        /// 按拓扑排序执行图中所有节点。
        /// <para>使用 Kahn 算法进行拓扑排序，然后按序执行每个节点。</para>
        /// </summary>
        public void ExecuteGraph(Dictionary<Guid, object?> context)
        {
            var sorted = TopologicalSort();
            foreach (var node in sorted)
            {
                if (node.IsEnabled && node.IsCompilable)
                {
                    node.Execute(context);
                }
            }
        }

        /// <summary>
        /// 事件驱动执行：从指定事件开始，沿执行流引脚追踪执行。
        /// </summary>
        public void ExecuteFromEvent(string eventName, Dictionary<Guid, object?> context)
        {
            var evt = Events.FirstOrDefault(e => e.EventName == eventName);
            if (evt == null) return;

            var startNode = GetNode(evt.NodeId);
            if (startNode == null) return;

            ExecuteExecutionFlow(startNode, context);
        }

        /// <summary>
        /// 从指定节点开始沿执行流追踪执行。
        /// </summary>
        private void ExecuteExecutionFlow(GraphNode startNode, Dictionary<Guid, object?> context)
        {
            var visited = new HashSet<Guid>();
            var queue = new Queue<GraphNode>();
            queue.Enqueue(startNode);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (!node.IsEnabled || !node.IsCompilable || visited.Contains(node.Id))
                    continue;

                visited.Add(node.Id);
                node.Execute(context);

                // 沿执行流输出引脚找到下一个节点
                foreach (var outputPin in node.OutputPins.Where(p => p.DataType == GraphPinDataType.Exec))
                {
                    foreach (var connId in outputPin.ConnectionIds)
                    {
                        var conn = GetConnection(connId);
                        if (conn != null)
                        {
                            var nextNode = GetNode(conn.TargetNodeId);
                            if (nextNode != null && !visited.Contains(nextNode.Id))
                            {
                                queue.Enqueue(nextNode);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 拓扑排序（Kahn 算法）。
        /// </summary>
        public List<GraphNode> TopologicalSort()
        {
            var nodeMap = Nodes.ToDictionary(n => n.Id);
            var inDegree = new Dictionary<Guid, int>();
            var adjacency = new Dictionary<Guid, List<Guid>>();

            foreach (var node in Nodes)
            {
                inDegree[node.Id] = 0;
                adjacency[node.Id] = new List<Guid>();
            }

            // 构建邻接表和入度
            foreach (var conn in Connections)
            {
                if (nodeMap.ContainsKey(conn.SourceNodeId) && nodeMap.ContainsKey(conn.TargetNodeId))
                {
                    adjacency[conn.SourceNodeId].Add(conn.TargetNodeId);
                    inDegree[conn.TargetNodeId]++;
                }
            }

            // Kahn 算法
            var queue = new Queue<Guid>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0) queue.Enqueue(kvp.Key);
            }

            var result = new List<GraphNode>();
            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                if (nodeMap.TryGetValue(nodeId, out var node))
                {
                    result.Add(node);
                }

                foreach (var next in adjacency[nodeId])
                {
                    inDegree[next]--;
                    if (inDegree[next] == 0)
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            // 如果结果数量 != 节点数量，说明存在循环
            return result;
        }

        // ───── 图编译 ─────

        /// <summary>
        /// 编译节点图，生成指定格式的输出。
        /// </summary>
        public string Compile(CompileOutputFormat format = CompileOutputFormat.CSharp)
        {
            // 验证图
            var validation = Validate();
            var errors = validation.Where(v => v.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                var errorMessages = string.Join("\n", errors.Select(e => e.Message));
                throw new InvalidOperationException($"编译失败，存在 {errors.Count} 个错误:\n{errorMessages}");
            }

            switch (format)
            {
                case CompileOutputFormat.CSharp:
                    return CompileToCSharp();

                case CompileOutputFormat.HLSL:
                    return CompileToHLSL();

                case CompileOutputFormat.IntermediateRepresentation:
                    return CompileToIR();

                case CompileOutputFormat.Bytecode:
                    return CompileToBytecode();

                default:
                    throw new ArgumentException($"不支持的编译输出格式: {format}");
            }
        }

        /// <summary>
        /// 编译为 C# 源代码。
        /// </summary>
        private string CompileToCSharp()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// 山雨引擎 - 自动生成的蓝图脚本");
            sb.AppendLine($"// 图: {Name} (v{Version})");
            sb.AppendLine($"// 生成时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Numerics;");
            sb.AppendLine("using ShanyuEngine.VisualScript;");
            sb.AppendLine();
            sb.AppendLine($"namespace ShanyuEngine.GeneratedScripts");
            sb.AppendLine("{");

            // 生成类
            var safeName = SanitizeIdentifier(Name);
            sb.AppendLine($"    public class {safeName}NodeScript");
            sb.AppendLine("    {");

            // 生成变量字段
            foreach (var variable in Variables)
            {
                var typeStr = DataTypeToCSharpType(variable.DataType);
                var access = variable.AccessLevel.ToString().ToLowerInvariant();
                sb.AppendLine($"        {access} {typeStr} {SanitizeIdentifier(variable.Name)}{GetDefaultValue(variable)};");
            }

            sb.AppendLine();

            // 生成事件方法
            foreach (var evt in Events)
            {
                var methodName = $"On_{SanitizeIdentifier(evt.EventName)}";
                sb.AppendLine($"        // 事件: {evt.EventName}");
                sb.AppendLine($"        public void {methodName}()");
                sb.AppendLine("        {");

                // 执行流追踪生成
                var startNode = GetNode(evt.NodeId);
                if (startNode != null)
                {
                    GenerateExecutionCode(sb, startNode, 12);
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // 生成函数方法
            foreach (var func in Functions)
            {
                var methodName = SanitizeIdentifier(func.FunctionName);
                var returnType = func.ReturnType.HasValue ? DataTypeToCSharpType(func.ReturnType.Value) : "void";
                var parameters = string.Join(", ", func.InputPins
                    .Where(p => p.DataType != GraphPinDataType.Exec && !p.IsOptional)
                    .Select(p => $"{DataTypeToCSharpType(p.DataType)} {SanitizeIdentifier(p.Name)}"));
                var optionalParams = string.Join(", ", func.InputPins
                    .Where(p => p.DataType != GraphPinDataType.Exec && p.IsOptional)
                    .Select(p => $"{DataTypeToCSharpType(p.DataType)} {SanitizeIdentifier(p.Name)} = {GetPinDefaultValue(p)}"));

                if (!string.IsNullOrEmpty(optionalParams))
                {
                    if (!string.IsNullOrEmpty(parameters))
                        parameters += ", ";
                    parameters += optionalParams;
                }

                sb.AppendLine($"        {func.AccessModifier.ToString().ToLowerInvariant()} {returnType} {methodName}({parameters})");
                sb.AppendLine("        {");

                if (func.ReturnType.HasValue)
                {
                    var retPin = func.OutputPins.FirstOrDefault(p => p.DataType != GraphPinDataType.Exec);
                    if (retPin != null)
                    {
                        sb.AppendLine($"            {DataTypeToCSharpType(func.ReturnType.Value)} result = default;");
                    }
                }

                // 执行流追踪生成
                var entryNode = GetNode(func.EntryNodeId);
                if (entryNode != null)
                {
                    GenerateExecutionCode(sb, entryNode, 12);
                }

                if (func.ReturnType.HasValue)
                {
                    sb.AppendLine("            return result;");
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 生成执行流代码。
        /// </summary>
        private void GenerateExecutionCode(System.Text.StringBuilder sb, GraphNode node, int indent)
        {
            var indentStr = new string(' ', indent);
            var visited = new HashSet<Guid>();
            GenerateExecutionCodeRecursive(sb, node, indent, visited);
        }

        private void GenerateExecutionCodeRecursive(System.Text.StringBuilder sb, GraphNode node, int indent, HashSet<Guid> visited)
        {
            if (visited.Contains(node.Id)) return;
            visited.Add(node.Id);

            var indentStr = new string(' ', indent);

            switch (node.NodeType)
            {
                case "PrintString":
                    sb.AppendLine($"{indentStr}System.Console.WriteLine(/* {node.Title} */);");
                    break;
                case "Branch":
                    var condPin = node.GetInputPin("Condition");
                    sb.AppendLine($"{indentStr}if ({GetPinValueRef(condPin, indentStr)})");
                    sb.AppendLine($"{indentStr}{{");
                    // True branch
                    var trueExecPin = node.GetOutputPin("True");
                    if (trueExecPin != null)
                    {
                        var trueNext = GetNextExecNode(trueExecPin);
                        if (trueNext != null) GenerateExecutionCodeRecursive(sb, trueNext, indent + 4, visited);
                    }
                    sb.AppendLine($"{indentStr}}}");
                    sb.AppendLine($"{indentStr}else");
                    sb.AppendLine($"{indentStr}{{");
                    // False branch
                    var falseExecPin = node.GetOutputPin("False");
                    if (falseExecPin != null)
                    {
                        var falseNext = GetNextExecNode(falseExecPin);
                        if (falseNext != null) GenerateExecutionCodeRecursive(sb, falseNext, indent + 4, visited);
                    }
                    sb.AppendLine($"{indentStr}}}");
                    return;
                case "SetVariable":
                    var varNamePin = node.GetOutputPin("Value");
                    var varName = node.Metadata.GetValueOrDefault("VariableName", "var");
                    sb.AppendLine($"{indentStr}{SanitizeIdentifier(varName)} = {GetPinValueRef(varNamePin, indentStr)};");
                    break;
                case "ForLoop":
                    sb.AppendLine($"{indentStr}for (int i = {GetPinValueRef(node.GetInputPin("Start"), indentStr)}; i < {GetPinValueRef(node.GetInputPin("End"), indentStr)}; i++)");
                    sb.AppendLine($"{indentStr}{{");
                    var loopBodyPin = node.GetOutputPin("LoopBody");
                    if (loopBodyPin != null)
                    {
                        var loopNext = GetNextExecNode(loopBodyPin);
                        if (loopNext != null) GenerateExecutionCodeRecursive(sb, loopNext, indent + 4, visited);
                    }
                    sb.AppendLine($"{indentStr}}}");
                    return;
                case "WhileLoop":
                    sb.AppendLine($"{indentStr}while ({GetPinValueRef(node.GetInputPin("Condition"), indentStr)})");
                    sb.AppendLine($"{indentStr}{{");
                    var bodyPin = node.GetOutputPin("LoopBody");
                    if (bodyPin != null)
                    {
                        var bodyNext = GetNextExecNode(bodyPin);
                        if (bodyNext != null) GenerateExecutionCodeRecursive(sb, bodyNext, indent + 4, visited);
                    }
                    sb.AppendLine($"{indentStr}}}");
                    return;
                case "CallFunction":
                    var funcName = node.Metadata.GetValueOrDefault("FunctionName", "Unknown");
                    var args = node.InputPins
                        .Where(p => p.DataType != GraphPinDataType.Exec)
                        .Select(p => GetPinValueRef(p, indentStr));
                    sb.AppendLine($"{indentStr}{funcName}({string.Join(", ", args)});");
                    break;
                default:
                    sb.AppendLine($"{indentStr}// TODO: {node.NodeType} - {node.Title}");
                    break;
            }

            // 沿执行流继续
            var thenPin = node.OutputPins.FirstOrDefault(p => p.DataType == GraphPinDataType.Exec);
            if (thenPin != null)
            {
                var nextNode = GetNextExecNode(thenPin);
                if (nextNode != null)
                {
                    GenerateExecutionCodeRecursive(sb, nextNode, indent, visited);
                }
            }
        }

        /// <summary>
        /// 获取执行流引脚连接的下一个节点。
        /// </summary>
        private GraphNode? GetNextExecNode(GraphPin execPin)
        {
            foreach (var connId in execPin.ConnectionIds)
            {
                var conn = GetConnection(connId);
                if (conn != null)
                {
                    return GetNode(conn.TargetNodeId);
                }
            }
            return null;
        }

        /// <summary>
        /// 获取引脚值引用代码。
        /// </summary>
        private string GetPinValueRef(GraphPin? pin, string indent)
        {
            if (pin == null) return "/* null */";

            if (pin.IsConnected)
            {
                foreach (var connId in pin.ConnectionIds)
                {
                    var conn = GetConnection(connId);
                    if (conn != null && conn.SourceNodeId != Guid.Empty)
                    {
                        var srcNode = GetNode(conn.SourceNodeId);
                        var srcPin = srcNode?.GetPin(conn.SourcePinId);
                        return srcPin != null ? $"{SanitizeIdentifier(srcPin.Name)}_{srcNode!.Id:N4}" : "/* unknown */";
                    }
                }
            }

            // 使用默认值
            if (!string.IsNullOrEmpty(pin.DefaultValue))
            {
                return pin.DefaultValue;
            }

            return GetDefaultForType(pin.DataType);
        }

        /// <summary>
        /// 编译为 HLSL 着色器代码（占位实现）。
        /// </summary>
        private string CompileToHLSL()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// 山雨引擎 - 自动生成的 HLSL 着色器");
            sb.AppendLine($"// 图: {Name}");
            sb.AppendLine();
            sb.AppendLine("// TODO: 实现 HLSL 代码生成");
            sb.AppendLine("// 支持从节点图生成顶点着色器、像素着色器等");
            return sb.ToString();
        }

        /// <summary>
        /// 编译为中间表示（IR）。
        /// </summary>
        private string CompileToIR()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("; 山雨引擎 - 节点图中间表示 (IR)");
            sb.AppendLine($"; 图: {Name} (v{Version})");
            sb.AppendLine();

            foreach (var node in TopologicalSort())
            {
                sb.AppendLine($"; Node: {node.Title} ({node.NodeType})");
                sb.AppendLine($"  NODE {node.Id:N} {node.NodeType}");

                foreach (var pin in node.InputPins)
                {
                    sb.AppendLine($"    INPUT {pin.Id:N} {pin.Name}:{pin.DataType}");
                }
                foreach (var pin in node.OutputPins)
                {
                    sb.AppendLine($"    OUTPUT {pin.Id:N} {pin.Name}:{pin.DataType}");
                }
                sb.AppendLine();
            }

            foreach (var conn in Connections)
            {
                sb.AppendLine($"  CONNECT {conn.SourcePinId:N} -> {conn.TargetPinId:N}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 编译为字节码（占位实现）。
        /// </summary>
        private string CompileToBytecode()
        {
            return $"// 山雨引擎 - 节点图字节码 (占位)\n// 图: {Name}\n// 节点数: {Nodes.Count}\n// 连接数: {Connections.Count}";
        }

        // ───── 图验证 ─────

        /// <summary>
        /// 验证节点图完整性。
        /// <para>检查断开引脚、循环依赖、类型匹配等问题。</para>
        /// </summary>
        public List<GraphValidationMessage> Validate()
        {
            var messages = new List<GraphValidationMessage>();

            // 1. 断开引脚检测：非可选输入引脚未连接且无默认值
            foreach (var node in Nodes)
            {
                foreach (var pin in node.InputPins)
                {
                    if (pin.DataType == GraphPinDataType.Exec) continue; // 执行流引脚另行处理

                    if (!pin.IsConnected && !pin.IsOptional && string.IsNullOrEmpty(pin.DefaultValue))
                    {
                        messages.Add(new GraphValidationMessage
                        {
                            Severity = ValidationSeverity.Warning,
                            NodeId = node.Id,
                            PinId = pin.Id,
                            Message = $"节点 '{node.Title}' 的输入引脚 '{pin.Name}' 未连接且无默认值",
                            Suggestion = $"请连接 '{pin.Name}' 引脚或设置默认值"
                        });
                    }
                }

                // 事件节点的执行流输出应连接
                if (node.NodeType.StartsWith("Event") || Events.Any(e => e.NodeId == node.Id))
                {
                    var execOutputs = node.OutputPins.Where(p => p.DataType == GraphPinDataType.Exec).ToList();
                    if (execOutputs.Count > 0 && execOutputs.All(p => !p.IsConnected))
                    {
                        messages.Add(new GraphValidationMessage
                        {
                            Severity = ValidationSeverity.Info,
                            NodeId = node.Id,
                            Message = $"事件节点 '{node.Title}' 的执行流输出未连接到任何节点",
                            Suggestion = "如果该事件为空，可以安全忽略此警告"
                        });
                    }
                }
            }

            // 2. 类型匹配检查
            foreach (var conn in Connections)
            {
                var srcNode = GetNode(conn.SourceNodeId);
                var tgtNode = GetNode(conn.TargetNodeId);
                if (srcNode == null || tgtNode == null)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        ConnectionId = conn.Id,
                        Message = $"连接引用了不存在的节点 (Source:{conn.SourceNodeId}, Target:{conn.TargetNodeId})",
                        Suggestion = "请删除此无效连接"
                    });
                    continue;
                }

                var srcPin = srcNode.GetPin(conn.SourcePinId);
                var tgtPin = tgtNode.GetPin(conn.TargetPinId);
                if (srcPin == null || tgtPin == null)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        ConnectionId = conn.Id,
                        Message = $"连接引用了不存在的引脚 (Source:{conn.SourcePinId}, Target:{conn.TargetPinId})",
                        Suggestion = "请删除此无效连接"
                    });
                    continue;
                }

                // 检查类型兼容性
                if (!AreTypesCompatible(srcPin.DataType, tgtPin.DataType))
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        ConnectionId = conn.Id,
                        NodeId = tgtNode.Id,
                        PinId = tgtPin.Id,
                        Message = $"类型不匹配: '{srcPin.Name}' ({srcPin.DataType}) -> '{tgtPin.Name}' ({tgtPin.DataType})",
                        Suggestion = $"请插入类型转换节点，或修改引脚类型"
                    });
                }
            }

            // 3. 循环检测
            var cycleNodes = DetectCycles();
            foreach (var cycleNodeId in cycleNodes)
            {
                var node = GetNode(cycleNodeId);
                if (node != null)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Error,
                        NodeId = cycleNodeId,
                        Message = $"节点 '{node.Title}' 参与了数据流循环，可能导致无限递归",
                        Suggestion = "请断开循环中的连接，或使用 ForLoop/WhileLoop 等显式循环节点"
                    });
                }
            }

            // 4. 孤立节点检查
            foreach (var node in Nodes)
            {
                var hasConnection = Connections.Any(c =>
                    c.SourceNodeId == node.Id || c.TargetNodeId == node.Id);
                if (!hasConnection && node.IsCompilable)
                {
                    messages.Add(new GraphValidationMessage
                    {
                        Severity = ValidationSeverity.Info,
                        NodeId = node.Id,
                        Message = $"节点 '{node.Title}' 是孤立节点，未连接到任何其他节点",
                        Suggestion = "如果该节点不再需要，可以安全删除"
                    });
                }
            }

            // 5. 变量名称唯一性检查
            var varNames = Variables.GroupBy(v => v.Name).Where(g => g.Count() > 1);
            foreach (var group in varNames)
            {
                messages.Add(new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Error,
                    Message = $"存在重复的变量名: '{group.Key}'",
                    Suggestion = "请重命名其中一个变量以避免冲突"
                });
            }

            // 6. 函数名称唯一性检查
            var funcNames = Functions.GroupBy(f => f.FunctionName).Where(g => g.Count() > 1);
            foreach (var group in funcNames)
            {
                messages.Add(new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Error,
                    Message = $"存在重复的函数名: '{group.Key}'",
                    Suggestion = "请重命名其中一个函数以避免冲突"
                });
            }

            return messages;
        }

        /// <summary>
        /// 使用 DFS 检测数据流循环。
        /// </summary>
        private List<Guid> DetectCycles()
        {
            var cycleNodes = new HashSet<Guid>();
            var nodeMap = Nodes.ToDictionary(n => n.Id);
            var visited = new HashSet<Guid>();
            var recursionStack = new HashSet<Guid>();

            foreach (var node in Nodes)
            {
                if (DFS_CycleDetect(node.Id, nodeMap, visited, recursionStack, cycleNodes))
                {
                    // cycleNodes 中已记录
                }
            }

            return cycleNodes.ToList();
        }

        private bool DFS_CycleDetect(
            Guid nodeId,
            Dictionary<Guid, GraphNode> nodeMap,
            HashSet<Guid> visited,
            HashSet<Guid> recursionStack,
            HashSet<Guid> cycleNodes)
        {
            if (recursionStack.Contains(nodeId))
            {
                cycleNodes.Add(nodeId);
                return true;
            }

            if (visited.Contains(nodeId)) return false;

            visited.Add(nodeId);
            recursionStack.Add(nodeId);

            // 找到从该节点出发的所有连接
            var outConnections = Connections.Where(c => c.SourceNodeId == nodeId);
            foreach (var conn in outConnections)
            {
                if (conn.TargetNodeId != nodeId) // 不考虑自连接
                {
                    DFS_CycleDetect(conn.TargetNodeId, nodeMap, visited, recursionStack, cycleNodes);
                }
            }

            recursionStack.Remove(nodeId);
            return false;
        }

        // ───── 图序列化 / 反序列化 ─────

        /// <summary>
        /// 将节点图序列化为 JSON 字符串。
        /// </summary>
        public string SerializeToJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Serialize(this, options);
        }

        /// <summary>
        /// 从 JSON 字符串反序列化节点图。
        /// </summary>
        public static NodeGraph? DeserializeFromJson(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Deserialize<NodeGraph>(json, options);
        }

        /// <summary>
        /// 将节点图保存到文件。
        /// </summary>
        public void SaveToFile(string filePath)
        {
            var json = SerializeToJson();
            System.IO.File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 从文件加载节点图。
        /// </summary>
        public static NodeGraph? LoadFromFile(string filePath)
        {
            var json = System.IO.File.ReadAllText(filePath);
            return DeserializeFromJson(json);
        }

        // ───── 辅助方法 ─────

        /// <summary>
        /// 获取数据类型对应的颜色（十六进制字符串）。
        /// </summary>
        public static string GetDataTypeColorHex(GraphPinDataType type)
        {
            return type switch
            {
                GraphPinDataType.Exec => "#FFFFFF",
                GraphPinDataType.Bool => "#CC0000",
                GraphPinDataType.Byte => "#990000",
                GraphPinDataType.Int => "#00CCCC",
                GraphPinDataType.Float => "#00CC66",
                GraphPinDataType.Double => "#00FF66",
                GraphPinDataType.String => "#FF00FF",
                GraphPinDataType.Name => "#CC00CC",
                GraphPinDataType.Enum => "#0066CC",
                GraphPinDataType.Vector2 => "#FFCC00",
                GraphPinDataType.Vector3 => "#FFCC00",
                GraphPinDataType.Vector4 => "#FFCC00",
                GraphPinDataType.Rotator => "#9966FF",
                GraphPinDataType.Quaternion => "#9966FF",
                GraphPinDataType.Transform => "#CC6600",
                GraphPinDataType.Color => "#FF6600",
                GraphPinDataType.Object => "#0000CC",
                GraphPinDataType.Array => "#FFFFFF",
                GraphPinDataType.Map => "#FFFFFF",
                GraphPinDataType.Set => "#FFFFFF",
                GraphPinDataType.Struct => "#0066FF",
                GraphPinDataType.Wildcard => "#888888",
                _ => "#FFFFFF"
            };
        }

        /// <summary>
        /// 检查两种数据类型是否兼容（可直接连接）。
        /// </summary>
        public static bool AreTypesCompatible(GraphPinDataType source, GraphPinDataType target)
        {
            if (source == target) return true;
            if (source == GraphPinDataType.Wildcard || target == GraphPinDataType.Wildcard) return true;
            if (source == GraphPinDataType.Exec || target == GraphPinDataType.Exec) return source == target;

            // 数值类型可隐式转换
            var numericTypes = new HashSet<GraphPinDataType>
            {
                GraphPinDataType.Byte, GraphPinDataType.Int,
                GraphPinDataType.Float, GraphPinDataType.Double
            };
            if (numericTypes.Contains(source) && numericTypes.Contains(target)) return true;

            // Vector3 可兼容 Rotator 和 Quaternion（需显式转换）
            if ((source == GraphPinDataType.Vector3 && target == GraphPinDataType.Rotator) ||
                (source == GraphPinDataType.Vector3 && target == GraphPinDataType.Quaternion)) return false; // 需要显式转换节点

            return false;
        }

        /// <summary>
        /// 将图数据类型转换为 C# 类型字符串。
        /// </summary>
        public static string DataTypeToCSharpType(GraphPinDataType type)
        {
            return type switch
            {
                GraphPinDataType.Bool => "bool",
                GraphPinDataType.Byte => "byte",
                GraphPinDataType.Int => "int",
                GraphPinDataType.Float => "float",
                GraphPinDataType.Double => "double",
                GraphPinDataType.String => "string",
                GraphPinDataType.Name => "string",
                GraphPinDataType.Enum => "int",
                GraphPinDataType.Vector2 => "Vector2",
                GraphPinDataType.Vector3 => "Vector3",
                GraphPinDataType.Vector4 => "Vector4",
                GraphPinDataType.Rotator => "Vector3",
                GraphPinDataType.Quaternion => "Quaternion",
                GraphPinDataType.Transform => "Matrix4x4",
                GraphPinDataType.Color => "Vector4",
                GraphPinDataType.Object => "object",
                GraphPinDataType.Array => "Array",
                GraphPinDataType.Map => "Dictionary",
                GraphPinDataType.Set => "HashSet",
                GraphPinDataType.Struct => "object",
                _ => "void"
            };
        }

        /// <summary>
        /// 获取变量默认值表达式。
        /// </summary>
        private static string GetDefaultValue(GraphVariable variable)
        {
            if (string.IsNullOrEmpty(variable.DefaultValue)) return string.Empty;
            return $" = {variable.DefaultValue}";
        }

        /// <summary>
        /// 获取引脚默认值表达式。
        /// </summary>
        private static string GetPinDefaultValue(GraphPin pin)
        {
            if (!string.IsNullOrEmpty(pin.DefaultValue)) return pin.DefaultValue;
            return GetDefaultForType(pin.DataType);
        }

        /// <summary>
        /// 获取类型的默认值表达式。
        /// </summary>
        private static string GetDefaultForType(GraphPinDataType type)
        {
            return type switch
            {
                GraphPinDataType.Bool => "false",
                GraphPinDataType.Byte => "(byte)0",
                GraphPinDataType.Int => "0",
                GraphPinDataType.Float => "0.0f",
                GraphPinDataType.Double => "0.0",
                GraphPinDataType.String => "string.Empty",
                GraphPinDataType.Vector2 => "Vector2.Zero",
                GraphPinDataType.Vector3 => "Vector3.Zero",
                GraphPinDataType.Vector4 => "Vector4.Zero",
                GraphPinDataType.Quaternion => "Quaternion.Identity",
                GraphPinDataType.Object => "null",
                _ => "default"
            };
        }

        /// <summary>
        /// 清理标识符名称使其成为合法的 C# 标识符。
        /// </summary>
        private static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_unnamed";

            // 替换非法字符
            var result = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('_');
                }
            }

            // 确保不以数字开头
            if (result.Length > 0 && char.IsDigit(result[0]))
            {
                result.Insert(0, '_');
            }

            return result.Length > 0 ? result.ToString() : "_unnamed";
        }

        /// <summary>
        /// 清空图中所有节点、连接、变量、事件和函数。
        /// </summary>
        public void Clear()
        {
            Nodes.Clear();
            Connections.Clear();
            Variables.Clear();
            Events.Clear();
            Functions.Clear();
            Metadata.Clear();
            Version = 0;
        }

        /// <summary>
        /// 获取图统计信息。
        /// </summary>
        public string GetStatistics()
        {
            return $"图 '{Name}': {Nodes.Count} 节点, {Connections.Count} 连接, {Variables.Count} 变量, {Events.Count} 事件, {Functions.Count} 函数";
        }
    }
}
