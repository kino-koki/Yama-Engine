using System;
using System.Numerics;
using ImGuiNET;
using YamaEngine.VisualScript;

namespace YamaEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 蓝图编辑器面板 (BlueprintEditorPanel)
    //
    // 提供可视化蓝图编辑器交互 UX:
    //   - 蓝图资产打开/新建（BlueprintAsset → BlueprintClass.EventGraph）
    //   - 节点画布控制（VisualScriptEditor 驱动：添加节点/撤销/重做/整理布局）
    //   - 编译预览（C# 代码输出 + 校验错误列表）
    //   - 节点列表 + 变量列表概览
    //   - 事件图表（EventGraph）与蓝图类互操作（实例化 + BeginPlay 触发）
    //
    // 依赖 VisualScriptEditor（VisualScriptEditorSystem.cs 完整编辑器数据模型/交互系统）。
    // 运行时执行（GraphNode.Execute）已闭环，本面板提供编辑器交互层。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>蓝图编辑器面板: 节点画布 + 事件图表 + 蓝图类互操作。</summary>
    public sealed class BlueprintEditorPanel : EditorPanel
    {
        /// <summary>蓝图编辑器核心（节点画布/连线/菜单/搜索/撤销/布局）。</summary>
        public VisualScriptEditor Editor { get; } = new VisualScriptEditor();

        /// <summary>当前打开的蓝图资产（null = 未打开）。</summary>
        public BlueprintAsset? CurrentAsset { get; private set; }

        /// <summary>最近一次编译输出。</summary>
        public string? LastCompileOutput { get; private set; }

        /// <summary>最近一次编译是否成功。</summary>
        public bool LastCompileSuccess { get; private set; }

        private bool _showNodes = true;
        private bool _showVariables = true;
        private bool _showCompile = true;
        private string _newBlueprintName = "BP_NewBlueprint";
        private string _compileOutputPreview = "";

        public BlueprintEditorPanel() : base("蓝图编辑器", "BlueprintEditor")
        {
            CanClose = true;
            MinSize = new Vector2(320, 260);
        }

        // ─────────────────────────── 蓝图资产操作 ───────────────────────────

        /// <summary>打开蓝图资产（将事件图绑定到编辑器核心）。</summary>
        public void OpenAsset(BlueprintAsset asset)
        {
            CurrentAsset = asset ?? throw new ArgumentNullException(nameof(asset));
            Editor.CurrentGraph = asset.Class.EventGraph;
            LastCompileOutput = null;
            _compileOutputPreview = "";
        }

        /// <summary>新建蓝图资产并打开。</summary>
        public BlueprintAsset NewBlueprint(string name)
        {
            var asset = BlueprintAsset.Create(name);
            OpenAsset(asset);
            return asset;
        }

        /// <summary>在当前图中添加节点（经 VisualScriptEditor，支持撤销）。</summary>
        public GraphNode? AddNode(string nodeType, Vector2 canvasPosition)
        {
            return Editor.AddNodeAtPosition(nodeType, canvasPosition);
        }

        /// <summary>触发编译预览。</summary>
        public void Compile()
        {
            if (Editor.CurrentGraph == null)
            {
                LastCompileOutput = "未打开蓝图。";
                LastCompileSuccess = false;
                return;
            }
            var result = Editor.CompilePreview();
            LastCompileSuccess = result.success;
            LastCompileOutput = result.output;
            _compileOutputPreview = result.output;
        }

        /// <summary>实例化当前蓝图并触发 BeginPlay（蓝图类互操作验证）。</summary>
        public BlueprintInstance? InstantiateAndBeginPlay()
        {
            if (CurrentAsset == null) return null;
            var instance = CurrentAsset.Class.Instantiate();
            instance.BeginPlay();
            return instance;
        }

        // ─────────────────────────── 面板绘制 ───────────────────────────

        protected override void OnDraw(EditorContext ctx)
        {
            DrawToolbar();
            ImGui.Separator();

            if (CurrentAsset == null || Editor.CurrentGraph == null)
            {
                DrawNewBlueprint();
                return;
            }

            if (_showNodes) DrawNodeList();
            if (_showVariables) DrawVariableList();
            if (_showCompile) DrawCompileSection();

            ImGui.Separator();
            ImGui.TextDisabled($"事件图: {CurrentAsset.Class.EventGraph.Nodes.Count} 节点 / " +
                               $"{CurrentAsset.Class.EventGraph.Connections.Count} 连线");
        }

        private void DrawToolbar()
        {
            // 新建蓝图
            ImGui.InputText("##bp_new_name", ref _newBlueprintName, 64);
            ImGui.SameLine();
            if (ImGui.Button("新建蓝图##bp_new"))
            {
                if (!string.IsNullOrWhiteSpace(_newBlueprintName))
                    NewBlueprint(_newBlueprintName.Trim());
            }

            if (CurrentAsset == null) return;

            ImGui.SameLine();
            if (ImGui.Button("撤销##bp_undo")) Editor.Undo();
            ImGui.SameLine();
            if (ImGui.Button("重做##bp_redo")) Editor.Redo();
            ImGui.SameLine();
            if (ImGui.Button("整理布局##bp_tidy")) Editor.TidyLayout();
            ImGui.SameLine();
            if (ImGui.Button("编译##bp_compile")) Compile();
            ImGui.SameLine();
            if (ImGui.Button("运行##bp_run"))
            {
                var inst = InstantiateAndBeginPlay();
                _compileOutputPreview = inst == null
                    ? "实例化失败（未打开蓝图）。"
                    : $"BeginPlay 执行完成，日志条目: {inst.RuntimeLog.Count}";
            }
        }

        private void DrawNewBlueprint()
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  未打开蓝图。");
            ImGui.TextDisabled("  输入名称并点击「新建蓝图」创建，或调用 OpenAsset 打开。");
        }

        private void DrawNodeList()
        {
            ImGui.Checkbox("节点列表", ref _showNodes);
            if (!_showNodes || Editor.CurrentGraph == null) return;

            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  节点");
            foreach (var node in Editor.CurrentGraph.Nodes)
            {
                ImGui.Text($"    [{node.NodeType}] {node.Title} @ ({node.Position.X:F0},{node.Position.Y:F0})");
            }
        }

        private void DrawVariableList()
        {
            ImGui.Checkbox("变量列表", ref _showVariables);
            if (!_showVariables || CurrentAsset == null) return;

            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.85f, 1f), "  蓝图变量");
            if (CurrentAsset.Class.Variables.Count == 0)
            {
                ImGui.TextDisabled("    （无变量）");
                return;
            }
            foreach (var v in CurrentAsset.Class.Variables)
            {
                ImGui.Text($"    {v.Name} : {v.Type} = '{v.DefaultValueText}'");
            }
        }

        private void DrawCompileSection()
        {
            ImGui.Checkbox("编译预览", ref _showCompile);
            if (!_showCompile) return;

            if (_compileOutputPreview.Length > 0)
            {
                ImGui.TextColored(LastCompileSuccess
                    ? new Vector4(0.3f, 0.85f, 0.45f, 1f)
                    : new Vector4(0.9f, 0.4f, 0.4f, 1f),
                    LastCompileSuccess ? "  编译成功" : "  编译失败");

                int previewLen = Math.Min(_compileOutputPreview.Length, 400);
                ImGui.TextWrapped(_compileOutputPreview.Substring(0, previewLen));
            }
            else
            {
                ImGui.TextDisabled("  点击「编译」生成 C# 预览。");
            }
        }
    }
}
