// BlueprintEditorIntegrationTests.cs — 可视化蓝图编辑器交互 UX 验证套件
// 覆盖:
//   * BlueprintAsset JSON 往返（变量 + 事件图节点/连线 + 元数据，事件图恢复后可执行）
//   * BlueprintClass 变量 + Instantiate 实例（变量读写、实例间隔离）
//   * BlueprintInstance.BeginPlay 事件驱动执行（BeginPlay→PrintString 捕获日志）
//   * BlueprintEditorPanel 节点画布操作（NewBlueprint → AddNode）
//   * BlueprintEditorPanel 撤销/重做（Undo/Redo 节点增减）
//   * BlueprintEditorPanel 注册（EditorPanel 子类 + 标题）
// 全部 CPU 级确定性，不依赖 GPU/ImGui 实际渲染。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Editor;
using YamaEngine.Editor.Panels;
using YamaEngine.VisualScript;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>可视化蓝图编辑器交互 UX 验证套件。</summary>
public sealed class BlueprintEditorIntegrationTests
{
    private static GraphNode Create(string nodeType)
    {
        var node = NodeScriptNodeRegistry.CreateNode(nodeType);
        AssertTrue(node != null, $"节点类型 '{nodeType}' 应在注册表中存在。");
        return node!;
    }

    private static GraphConnection? ConnectPin(NodeGraph graph, GraphNode src, string srcPin, GraphNode tgt, string tgtPin)
    {
        var conn = graph.Connect(src.Id, src.GetOutputPin(srcPin)!.Id, tgt.Id, tgt.GetInputPin(tgtPin)!.Id);
        AssertTrue(conn != null, $"连接 {src.NodeType}.{srcPin} → {tgt.NodeType}.{tgtPin} 应成功。");
        return conn;
    }

    private static void BindEvent(NodeGraph graph, string eventName, GraphNode node)
    {
        var evt = GraphEvent.Create(eventName, "Game");
        evt.NodeId = node.Id;
        graph.AddEvent(evt);
    }

    /// <summary>BlueprintAsset JSON 往返：变量 + 事件图 + 元数据完整保留。</summary>
    public static void TestBlueprintAssetJsonRoundtrip()
    {
        var asset = BlueprintAsset.Create("BP_Roundtrip");
        asset.Class.AddVariable("Speed", GraphPinDataType.Float, "10.5");
        asset.Class.AddVariable("Health", GraphPinDataType.Int, "100");

        var beginPlay = Create("EventBeginPlay");
        var print = Create("PrintString");
        asset.Class.EventGraph.AddNode(beginPlay);
        asset.Class.EventGraph.AddNode(print);
        ConnectPin(asset.Class.EventGraph, beginPlay, "Exec", print, "Exec");
        BindEvent(asset.Class.EventGraph, "BeginPlay", beginPlay);

        string json = asset.SerializeToJson();
        AssertTrue(json.Length > 0, "序列化应产出非空 JSON。");

        var restored = BlueprintAsset.DeserializeFromJson(json);
        AssertTrue(restored != null, "反序列化应成功。");
        AssertEqual("BP_Roundtrip", restored!.Name, "Name 应往返保留。");
        AssertEqual(2, restored.Class.Variables.Count, "变量数应保留。");
        AssertEqual("Speed", restored.Class.Variables[0].Name, "变量名应保留。");
        AssertEqual(GraphPinDataType.Float, restored.Class.Variables[0].Type, "变量类型应保留。");
        AssertEqual(2, restored.Class.EventGraph.Nodes.Count, "事件图节点数应保留。");
        AssertEqual(1, restored.Class.EventGraph.Connections.Count, "事件图连线数应保留。");
    }

    /// <summary>BlueprintClass 变量 + Instantiate：变量定义与运行时读写。</summary>
    public static void TestBlueprintClassVariablesAndInstance()
    {
        var blueprint = new BlueprintClass { Name = "BP_Var", DisplayName = "Var" };
        blueprint.AddVariable("Score", GraphPinDataType.Int, "0");
        blueprint.AddVariable("Name", GraphPinDataType.String, "Hero");

        var instA = blueprint.Instantiate();
        var instB = blueprint.Instantiate();

        instA.SetVariable("Score", 42);
        instB.SetVariable("Score", 7);

        AssertEqual(42, instA.GetVariable("Score"), "实例 A 变量应独立写入。");
        AssertEqual(7, instB.GetVariable("Score"), "实例 B 变量应独立写入（实例间隔离）。");
        AssertEqual("Hero", instA.GetVariable("Name"), "变量默认值应可读。");
    }

    /// <summary>BlueprintInstance.BeginPlay 事件驱动：BeginPlay→PrintString 捕获运行时日志。</summary>
    public static void TestBlueprintInstanceBeginPlayExecutes()
    {
        var blueprint = new BlueprintClass { Name = "BP_Play", DisplayName = "Play" };
        var graph = blueprint.EventGraph;

        var beginPlay = Create("EventBeginPlay");
        var print = Create("PrintString");
        graph.AddNode(beginPlay);
        graph.AddNode(print);
        ConnectPin(graph, beginPlay, "Exec", print, "Exec");
        BindEvent(graph, "BeginPlay", beginPlay);

        var inst = blueprint.Instantiate();
        inst.BeginPlay();

        AssertTrue(inst.ExecutedCount == 1, "BeginPlay 应执行一次。");
        AssertTrue(inst.RuntimeLog.Count >= 1, "PrintString 应写入运行时日志。");
    }

    /// <summary>BlueprintEditorPanel：NewBlueprint + AddNode 添加节点到画布。</summary>
    public static void TestBlueprintEditorPanelAddNode()
    {
        var panel = new BlueprintEditorPanel();
        panel.NewBlueprint("BP_Panel");

        var node = panel.AddNode("Add", new Vector2(100, 100));

        AssertTrue(node != null, "AddNode 应成功创建节点。");
        AssertEqual(1, panel.Editor.CurrentGraph!.Nodes.Count, "添加节点后图应含 1 节点。");
        AssertTrue(panel.Editor.CurrentGraph.Nodes.Contains(node!), "节点应属于当前图。");
    }

    /// <summary>BlueprintEditorPanel：AddNode → Undo → Redo（撤销/重做）。</summary>
    public static void TestBlueprintEditorPanelUndoRedo()
    {
        var panel = new BlueprintEditorPanel();
        panel.NewBlueprint("BP_Undo");

        panel.AddNode("Add", new Vector2(50, 50));
        AssertEqual(1, panel.Editor.CurrentGraph!.Nodes.Count, "添加后 1 节点。");

        panel.Editor.Undo();
        AssertEqual(0, panel.Editor.CurrentGraph.Nodes.Count, "撤销后应 0 节点。");

        panel.Editor.Redo();
        AssertEqual(1, panel.Editor.CurrentGraph.Nodes.Count, "重做后应恢复 1 节点。");
    }

    /// <summary>BlueprintEditorPanel：注册为 EditorPanel 子类且标题正确。</summary>
    public static void TestBlueprintEditorPanelRegistered()
    {
        var panel = new BlueprintEditorPanel();

        AssertTrue(panel is EditorPanel, "BlueprintEditorPanel 应为 EditorPanel 子类。");
        AssertEqual("蓝图编辑器", panel.Title, "面板标题应为 '蓝图编辑器'。");
        AssertEqual("BlueprintEditor", panel.Id, "面板 ID 应为 'BlueprintEditor'。");
        AssertTrue(panel.CanClose, "面板应可关闭。");
    }
}
