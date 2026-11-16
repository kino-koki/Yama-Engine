// BlueprintClassSystem.cs — 可视化蓝图类（蓝图类互操作 + 运行时实例化）
// ==================================================================================================
//  命名空间: YamaEngine.VisualScript
//  依赖:     NodeGraph / GraphNode / GraphPinDataType（运行时执行已由 GraphNode.Execute 闭环）
//
//  功能:
//    1. BlueprintVariable  — 蓝图变量定义（名称 + 类型 + 默认值文本）
//    2. BlueprintClass     — 蓝图类：持有事件图（NodeGraph）+ 变量列表，可实例化
//    3. BlueprintInstance  — 蓝图运行时实例：绑定事件图，BeginPlay 触发执行、变量读写
//    4. JSON 序列化（变量往返，图由 NodeGraph.SerializeToJson / DeserializeFromJson 处理）
// ==================================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.VisualScript;

/// <summary>蓝图变量定义。</summary>
public sealed class BlueprintVariable
{
    /// <summary>变量名。</summary>
    public string Name { get; set; } = "NewVar";

    /// <summary>变量数据类型。</summary>
    public GraphPinDataType Type { get; set; } = GraphPinDataType.Float;

    /// <summary>默认值文本（由 <see cref="NodeGraph.ParseDefaultValue"/> 解析）。</summary>
    public string DefaultValueText { get; set; } = "";

    /// <summary>解析默认值为强类型对象（失败回退 null）。</summary>
    public object? GetDefaultValue()
    {
        return NodeGraph.ParseDefaultValue(DefaultValueText, Type);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
    }

    public static BlueprintVariable? FromJson(string json)
    {
        return JsonSerializer.Deserialize<BlueprintVariable>(json);
    }
}

/// <summary>蓝图类（Blueprint Class）：持有事件图 + 变量，可实例化为运行时对象。</summary>
/// <remarks>提供 UE4.7 蓝图类互操作：一个蓝图类可被多次实例化，每实例持有自己的运行时状态。</remarks>
public sealed class BlueprintClass
{
    /// <summary>类名（资产内唯一标识，如 "BP_Player"）。</summary>
    public string Name { get; set; } = "BP_NewBlueprint";

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "New Blueprint";

    /// <summary>父类名（蓝图继承；空 = 根，UE4.7 Object 基类）。</summary>
    public string ParentClass { get; set; } = "";

    /// <summary>事件图（BeginPlay / Tick / 自定义事件等）。</summary>
    [JsonIgnore]
    public NodeGraph EventGraph { get; set; } = new NodeGraph { Name = "EventGraph", DisplayName = "Event Graph" };

    /// <summary>蓝图变量定义列表。</summary>
    public List<BlueprintVariable> Variables { get; set; } = new();

    // ── 图 JSON（EventGraph 由 NodeGraph 自身序列化，独立存储） ──

    /// <summary>事件图序列化（供蓝图资产持久化）。</summary>
    public string EventGraphJson => EventGraph.SerializeToJson();

    /// <summary>从 JSON 恢复事件图。</summary>
    public void LoadEventGraphJson(string json)
    {
        var loaded = NodeGraph.DeserializeFromJson(json);
        if (loaded != null)
            EventGraph = loaded;
    }

    /// <summary>添加变量定义。</summary>
    public BlueprintVariable AddVariable(string name, GraphPinDataType type, string defaultValueText = "")
    {
        var v = new BlueprintVariable { Name = name, Type = type, DefaultValueText = defaultValueText };
        Variables.Add(v);
        return v;
    }

    /// <summary>按名查找变量定义。</summary>
    public BlueprintVariable? GetVariable(string name)
    {
        return Variables.FirstOrDefault(v => v.Name == name);
    }

    /// <summary>实例化蓝图：创建独立的运行时实例（各实例运行时状态隔离）。</summary>
    public BlueprintInstance Instantiate()
    {
        return new BlueprintInstance(this);
    }
}

/// <summary>蓝图运行时实例：绑定蓝图类的事件图，提供 BeginPlay 触发与变量读写。</summary>
/// <remarks>运行时状态由 NodeGraph 的 Runtime* 字段持有，实例间相互隔离（各自持有独立 EventGraph 副本的运行时字段）。</remarks>
public sealed class BlueprintInstance
{
    private readonly BlueprintClass _blueprint;

    /// <summary>所属蓝图类。</summary>
    public BlueprintClass Blueprint => _blueprint;

    /// <summary>本实例的事件图（每次实例化复制类的事件图，保证运行时状态隔离）。</summary>
    public NodeGraph Graph { get; }

    /// <summary>运行时日志（PrintString 等节点写入）。</summary>
    public IReadOnlyList<string> RuntimeLog => Graph.RuntimeLog;

    /// <summary>已触发的执行（调试/测试可读）。</summary>
    public int ExecutedCount { get; private set; }

    public BlueprintInstance(BlueprintClass blueprint)
    {
        _blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
        // 复制事件图，保证多实例间运行时状态隔离
        var json = blueprint.EventGraphJson;
        Graph = NodeGraph.DeserializeFromJson(json) ?? new NodeGraph();
        Graph.Name = blueprint.EventGraph.Name;
        Graph.DisplayName = blueprint.EventGraph.DisplayName;
        // 同步蓝图类变量定义到图变量（运行时 GetVariableValue/SetVariableValue 依赖图变量注册）
        foreach (var v in blueprint.Variables)
        {
            if (Graph.GetVariable(v.Name) == null)
            {
                Graph.AddVariable(new GraphVariable
                {
                    Name = v.Name,
                    DataType = v.Type,
                    DefaultValue = v.DefaultValueText,
                });
            }
        }
    }

    /// <summary>触发 BeginPlay：重置运行时状态并执行事件图。</summary>
    /// <remarks>优先事件驱动（UE 语义）：从 BeginPlay 事件入口沿执行流执行；若图未绑定 BeginPlay 事件则回退全图拓扑执行。</remarks>
    public void BeginPlay()
    {
        Graph.ResetRuntime();
        var context = new Dictionary<Guid, object?>();
        bool hasBeginPlay = Graph.Events.Any(e => e.EventName == "BeginPlay");
        if (hasBeginPlay)
            Graph.ExecuteFromEvent("BeginPlay", context);
        else
            Graph.ExecuteGraph(context);
        ExecutedCount++;
    }

    /// <summary>读取变量运行时值。</summary>
    public object? GetVariable(string name) => Graph.GetVariableValue(name);

    /// <summary>设置变量运行时值。</summary>
    public void SetVariable(string name, object? value) => Graph.SetVariableValue(name, value);
}
