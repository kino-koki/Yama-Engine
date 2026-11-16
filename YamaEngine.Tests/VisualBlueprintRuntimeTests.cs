// VisualBlueprintRuntimeTests.cs — 可视化蓝图（节点图）运行时执行验证套件
// 覆盖:
//   * GraphNode.Execute 按 NodeType 分发 → 纯数据节点经 NodeScriptNodeExecutor 求值
//   * EventBeginPlay → SetVariable → PrintString 端到端执行链（图变量读写 + 日志捕获）
//   * 数据流：纯节点（Add/Multiply/ToString）通过连接把值喂给下游，即使不在执行流路径上
//   * Branch 条件执行（True/False 分支只激活选定输出）
//   * DoOnce 状态机（只触发一次）
//   * ExecuteGraph 拓扑排序执行全部节点
//   * NodeGraph.ParseDefaultValue 各类型解析
//   * 蓝图 JSON 序列化往返后仍可执行（运行时状态不参与序列化）
// 全部 CPU 级确定性, 不依赖 GPU/场景。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using YamaEngine.VisualScript;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>可视化蓝图节点图运行时执行验证套件。</summary>
public sealed class VisualBlueprintRuntimeTests
{
    private static NodeGraph MakeGraph(string name)
    {
        return new NodeGraph { Name = name, GraphType = "EventGraph" };
    }

    private static GraphNode Create(string nodeType)
    {
        var node = NodeScriptNodeRegistry.CreateNode(nodeType);
        AssertTrue(node != null, $"节点类型 '{nodeType}' 应在注册表中存在。");
        return node!;
    }

    private static void BindEvent(NodeGraph graph, string eventName, GraphNode node)
    {
        var evt = GraphEvent.Create(eventName, "Game");
        evt.NodeId = node.Id;
        graph.AddEvent(evt);
    }

    private static GraphConnection? ConnectPin(NodeGraph graph, GraphNode src, string srcPin, GraphNode tgt, string tgtPin)
    {
        var conn = graph.Connect(src.Id, src.GetOutputPin(srcPin)!.Id, tgt.Id, tgt.GetInputPin(tgtPin)!.Id);
        AssertTrue(conn != null, $"连接 {src.NodeType}.{srcPin} → {tgt.NodeType}.{tgtPin} 应成功。");
        return conn;
    }

    /// <summary>BeginPlay → SetVariable → PrintString：图变量写入并捕获日志。</summary>
    public static void TestBeginPlaySetVariablePrintStringChain()
    {
        var graph = MakeGraph("ChainBP");
        graph.AddVariable(new GraphVariable
        {
            Name = "PlayerName",
            DataType = GraphPinDataType.String,
            DefaultValue = "Player"
        });

        var beginPlay = Create("EventBeginPlay");
        var setVar = Create("SetVariable");
        setVar.Metadata["VariableName"] = "PlayerName";
        var print = Create("PrintString");

        graph.AddNode(beginPlay);
        graph.AddNode(setVar);
        graph.AddNode(print);

        ConnectPin(graph, beginPlay, "Exec", setVar, "Exec");
        ConnectPin(graph, setVar, "Then", print, "Exec");

        // SetVariable.Value ← ToString(GetVariable.PlayerName) 数据流
        var getVar = Create("GetVariable");
        getVar.Metadata["VariableName"] = "PlayerName";
        var toStr = Create("ToString");
        graph.AddNode(getVar);
        graph.AddNode(toStr);
        ConnectPin(graph, getVar, "Value", toStr, "Input");
        ConnectPin(graph, toStr, "Output", setVar, "Value");
        ConnectPin(graph, toStr, "Output", print, "String");

        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();
        graph.ExecuteFromEvent("BeginPlay", context);

        // SetVariable 应把 PlayerName 的当前值写回图变量（GetVariable 默认 Player）
        AssertEqual("Player", graph.GetVariableValue("PlayerName"), "图变量 PlayerName 应为 Player。");
        AssertTrue(graph.RuntimeLog.Count == 1, "PrintString 应打印一次。");
        AssertEqual("Player", graph.RuntimeLog[0], "PrintString 应输出 PlayerName 的当前值。");
    }

    /// <summary>BeginPlay → SetVariable → PrintString：SetVariable 写入纯节点计算值。</summary>
    public static void TestSetVariableStoresComputedValue()
    {
        var graph = MakeGraph("ComputeBP");
        graph.AddVariable(new GraphVariable
        {
            Name = "Score",
            DataType = GraphPinDataType.Float,
            DefaultValue = "0"
        });

        var beginPlay = Create("EventBeginPlay");
        var add = Create("Add");
        add.GetInputPin("A")!.DefaultValue = "3";
        add.GetInputPin("B")!.DefaultValue = "4";
        var setVar = Create("SetVariable");
        setVar.Metadata["VariableName"] = "Score";
        var print = Create("PrintString");

        graph.AddNode(beginPlay);
        graph.AddNode(add);
        graph.AddNode(setVar);
        graph.AddNode(print);

        // 数据流：Add.Result → SetVariable.Value；SetVariable.Output → ToString → PrintString.String
        var toStr = Create("ToString");
        graph.AddNode(toStr);
        ConnectPin(graph, add, "Result", setVar, "Value");
        ConnectPin(graph, setVar, "Output", toStr, "Input");
        ConnectPin(graph, toStr, "Output", print, "String");

        // 执行流：BeginPlay → SetVariable → PrintString
        ConnectPin(graph, beginPlay, "Exec", setVar, "Exec");
        ConnectPin(graph, setVar, "Then", print, "Exec");

        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();
        graph.ExecuteFromEvent("BeginPlay", context);

        // Add(3,4) = 7 写入 Score
        AssertEqual(7f, (float)graph.GetVariableValue("Score")!, "图变量 Score 应为 7。");
        AssertTrue(graph.RuntimeLog.Count == 1, "PrintString 应打印一次。");
        AssertEqual("7", graph.RuntimeLog[0], "PrintString 应输出计算后的 '7'。");
    }

    /// <summary>Branch 条件执行：True/False 分支只激活选定输出。</summary>
    public static void TestBranchConditionalExecution()
    {
        var graph = MakeGraph("BranchBP");

        var beginPlay = Create("EventBeginPlay");
        var branch = Create("Branch");
        var printTrue = Create("PrintString");
        printTrue.GetInputPin("String")!.DefaultValue = "TRUE";
        var printFalse = Create("PrintString");
        printFalse.GetInputPin("String")!.DefaultValue = "FALSE";

        graph.AddNode(beginPlay);
        graph.AddNode(branch);
        graph.AddNode(printTrue);
        graph.AddNode(printFalse);

        ConnectPin(graph, beginPlay, "Exec", branch, "Exec");
        ConnectPin(graph, branch, "True", printTrue, "Exec");
        ConnectPin(graph, branch, "False", printFalse, "Exec");

        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();

        // 条件 = true → 只走 True 分支
        branch.GetInputPin("Condition")!.DefaultValue = "true";
        graph.ExecuteFromEvent("BeginPlay", context);
        AssertTrue(graph.RuntimeLog.Count == 1 && graph.RuntimeLog[0] == "TRUE",
            $"True 分支应打印 'TRUE'（实际 [{string.Join(",", graph.RuntimeLog)}]）。");

        // 条件 = false → 只走 False 分支
        graph.ResetRuntime();
        branch.GetInputPin("Condition")!.DefaultValue = "false";
        graph.ExecuteFromEvent("BeginPlay", context);
        AssertTrue(graph.RuntimeLog.Count == 1 && graph.RuntimeLog[0] == "FALSE",
            $"False 分支应打印 'FALSE'（实际 [{string.Join(",", graph.RuntimeLog)}]）。");
    }

    /// <summary>DoOnce 状态机：执行流只触发一次。</summary>
    public static void TestDoOnceFiresOnlyOnce()
    {
        var graph = MakeGraph("DoOnceBP");

        var beginPlay = Create("EventBeginPlay");
        var doOnce = Create("DoOnce");
        var print = Create("PrintString");
        print.GetInputPin("String")!.DefaultValue = "FIRED";

        graph.AddNode(beginPlay);
        graph.AddNode(doOnce);
        graph.AddNode(print);

        ConnectPin(graph, beginPlay, "Exec", doOnce, "Exec");
        ConnectPin(graph, doOnce, "Exec", print, "Exec");

        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();

        graph.ExecuteFromEvent("BeginPlay", context);
        AssertTrue(graph.RuntimeLog.Count == 1 && graph.RuntimeLog[0] == "FIRED",
            "首次执行 DoOnce 应触发 PrintString。");

        // 再次执行：DoOnce 已触发，不应再打印
        graph.ExecuteFromEvent("BeginPlay", context);
        AssertTrue(graph.RuntimeLog.Count == 1,
            $"DoOnce 第二次不应再触发（日志数应为 1，实际 {graph.RuntimeLog.Count}）。");
    }

    /// <summary>数据流多跳：GetVariable → Multiply → ToString → PrintString（纯节点链）。</summary>
    public static void TestPureNodeDataFlowFeedsPrint()
    {
        var graph = MakeGraph("DataFlowBP");
        graph.AddVariable(new GraphVariable
        {
            Name = "Total",
            DataType = GraphPinDataType.Float,
            DefaultValue = "0"
        });

        var beginPlay = Create("EventBeginPlay");
        var setVar = Create("SetVariable");
        setVar.Metadata["VariableName"] = "Total";
        var add = Create("Add");
        add.GetInputPin("A")!.DefaultValue = "10";
        add.GetInputPin("B")!.DefaultValue = "5";

        var getVar = Create("GetVariable");
        getVar.Metadata["VariableName"] = "Total";
        var multiply = Create("Multiply");
        multiply.GetInputPin("B")!.DefaultValue = "2";
        var toStr = Create("ToString");
        var print = Create("PrintString");

        graph.AddNode(beginPlay);
        graph.AddNode(add);
        graph.AddNode(setVar);
        graph.AddNode(getVar);
        graph.AddNode(multiply);
        graph.AddNode(toStr);
        graph.AddNode(print);

        // 数据流：Add.Result → SetVariable.Value；GetVariable.Value → Multiply.A；Multiply.Result → ToString → Print
        ConnectPin(graph, add, "Result", setVar, "Value");
        ConnectPin(graph, getVar, "Value", multiply, "A");
        ConnectPin(graph, multiply, "Result", toStr, "Input");
        ConnectPin(graph, toStr, "Output", print, "String");

        // 执行流：BeginPlay → SetVariable → PrintString
        ConnectPin(graph, beginPlay, "Exec", setVar, "Exec");
        ConnectPin(graph, setVar, "Then", print, "Exec");

        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();
        graph.ExecuteFromEvent("BeginPlay", context);

        // Total = 10 + 5 = 15；Multiply(15,2) = 30 → 打印 "30"
        AssertEqual(15f, (float)graph.GetVariableValue("Total")!, "图变量 Total 应为 15。");
        AssertTrue(graph.RuntimeLog.Count == 1, "PrintString 应打印一次。");
        AssertEqual("30", graph.RuntimeLog[0], "Multiply(15,2) 应输出 '30'。");
    }

    /// <summary>ExecuteGraph 拓扑排序执行全部节点（含纯数据节点）。</summary>
    public static void TestExecuteGraphTopologicalComputation()
    {
        var graph = MakeGraph("TopoBP");
        graph.AddVariable(new GraphVariable
        {
            Name = "Sum",
            DataType = GraphPinDataType.Float,
            DefaultValue = "0"
        });

        var beginPlay = Create("EventBeginPlay");
        var add = Create("Add");
        add.GetInputPin("A")!.DefaultValue = "2";
        add.GetInputPin("B")!.DefaultValue = "3";
        var setVar = Create("SetVariable");
        setVar.Metadata["VariableName"] = "Sum";

        graph.AddNode(beginPlay);
        graph.AddNode(add);
        graph.AddNode(setVar);

        ConnectPin(graph, beginPlay, "Exec", setVar, "Exec");
        ConnectPin(graph, add, "Result", setVar, "Value");
        BindEvent(graph, "BeginPlay", beginPlay);

        var context = new Dictionary<Guid, object?>();
        graph.ExecuteGraph(context);

        AssertEqual(5f, (float)graph.GetVariableValue("Sum")!, "ExecuteGraph 应计算 Add(2,3)=5 并写入 Sum。");
    }

    /// <summary>ParseDefaultValue 各类型解析。</summary>
    public static void TestParseDefaultValueVariants()
    {
        AssertEqual(true, (bool)NodeGraph.ParseDefaultValue("true", GraphPinDataType.Bool)!, "bool 'true' 解析。");
        AssertEqual(false, (bool)NodeGraph.ParseDefaultValue("false", GraphPinDataType.Bool)!, "bool 'false' 解析。");
        AssertEqual(5, (int)NodeGraph.ParseDefaultValue("5", GraphPinDataType.Int)!, "int '5' 解析。");
        AssertEqual(3.5f, (float)NodeGraph.ParseDefaultValue("3.5", GraphPinDataType.Float)!, "float '3.5' 解析。");
        AssertEqual("Hello", (string)NodeGraph.ParseDefaultValue("Hello", GraphPinDataType.String)!, "string 解析。");

        var v3 = (Vector3)NodeGraph.ParseDefaultValue("(1,2,3)", GraphPinDataType.Vector3)!;
        AssertEqual(1f, v3.X, "Vector3 X 解析。");
        AssertEqual(2f, v3.Y, "Vector3 Y 解析。");
        AssertEqual(3f, v3.Z, "Vector3 Z 解析。");

        var col = (Color)NodeGraph.ParseDefaultValue("(0.5,0.25,1,1)", GraphPinDataType.Color)!;
        AssertEqual(0.5f, col.R, "Color R 解析。");
        AssertEqual(1f, col.A, "Color A 解析。");

        // 无默认值 → null
        AssertTrue(NodeGraph.ParseDefaultValue(null, GraphPinDataType.Float) == null, "null 默认值 → null。");
    }

    /// <summary>JSON 序列化往返后仍可执行（运行时状态不序列化）。</summary>
    public static void TestBlueprintJsonRoundtripPreservesExecution()
    {
        var graph = MakeGraph("RoundtripBP");
        graph.AddVariable(new GraphVariable
        {
            Name = "Health",
            DataType = GraphPinDataType.Float,
            DefaultValue = "100"
        });

        var beginPlay = Create("EventBeginPlay");
        var add = Create("Add");
        add.GetInputPin("A")!.DefaultValue = "10";
        add.GetInputPin("B")!.DefaultValue = "5";
        var setVar = Create("SetVariable");
        setVar.Metadata["VariableName"] = "Health";
        var print = Create("PrintString");

        graph.AddNode(beginPlay);
        graph.AddNode(add);
        graph.AddNode(setVar);
        graph.AddNode(print);
        ConnectPin(graph, add, "Result", setVar, "Value");
        ConnectPin(graph, beginPlay, "Exec", setVar, "Exec");
        ConnectPin(graph, setVar, "Then", print, "Exec");
        BindEvent(graph, "BeginPlay", beginPlay);

        // 序列化 → 反序列化
        var json = graph.SerializeToJson();
        var restored = NodeGraph.DeserializeFromJson(json);
        AssertTrue(restored != null, "反序列化应成功。");
        AssertTrue(restored!.Nodes.Count == 4, "反序列化后应保留 4 个节点。");

        // 反序列化后仍需绑定事件（事件节点 ID 保留）
        var restoredBegin = restored.FindNodesByType("EventBeginPlay")[0];
        var restoredSet = restored.FindNodesByType("SetVariable")[0];
        restoredSet.Metadata["VariableName"] = "Health";
        BindEvent(restored, "BeginPlay", restoredBegin);

        var context = new Dictionary<Guid, object?>();
        restored.ExecuteFromEvent("BeginPlay", context);

        AssertEqual(15f, (float)restored.GetVariableValue("Health")!, "往返后 Add(10,5)=15 应写入 Health。");
    }
}
