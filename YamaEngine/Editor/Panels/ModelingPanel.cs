// ==================================================================================================
//  ModelingPanel.cs — yama引擎内置建模工具面板
//
//  命名空间: YamaEngine.Editor.Panels
//  依赖:     ImGuiNET, YamaEngine.Modeling
//
//  功能:
//    引擎内置建模工具，类似 Blender Edit Mode 但直接集成到编辑器中。
//    1. 基础体生成 (Cube/Sphere/Cylinder/Plane/Cone/Torus)
//    2. 网格编辑模式 (顶点/边/面选择)
//    3. 编辑操作 (挤出/内缩/倒角/环切/桥接/合并)
//    4. 变换工具 (移动/旋转/缩放选中元素)
//    5. UV 工具 (自动展开/展开投影)
//    6. 法线工具 (翻转/重计算/平滑)
//    7. 网格验证 (流形检查/孤立顶点检测)
//    8. 导出 (FBX/OBJ/glTF)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace YamaEngine.Editor.Panels
{
    /// <summary>
    /// 建模工具面板。提供基础体生成、网格编辑、变换工具和网格验证功能。
    /// </summary>
    public sealed class ModelingPanel : EditorPanel
    {
        // ── 编辑模式 ──────────────────────────────────────────────────────────

        private MeshEditMode _editMode = MeshEditMode.Object;
        private PrimitiveType _pendingPrimitive = PrimitiveType.None;

        // ── 当前操作 ──────────────────────────────────────────────────────────

        private ModelingTool _activeTool = ModelingTool.None;
        private float _extrudeDistance = 1.0f;
        private float _insetAmount = 0.1f;
        private float _bevelWidth = 0.1f;
        private int _bevelSegments = 2;
        private int _loopCutPosition = 50; // 百分比

        // ── 选择模式 ──────────────────────────────────────────────────────────

        private bool _selectMultiple = true;
        private bool _softSelect = false;
        private float _softSelectRadius = 2f;

        // ── 网格统计 ──────────────────────────────────────────────────────────

        private int _vertexCount = 0;
        private int _edgeCount = 0;
        private int _faceCount = 0;
        private int _selectedVertCount = 0;

        // ── UV 设置 ──────────────────────────────────────────────────────────

        private int _uvChannel = 0;
        private float _uvScale = 1.0f;

        // ── 颜色 ──────────────────────────────────────────────────────────────

        private static readonly uint ColVertex = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.3f, 1f));
        private static readonly uint ColEdge = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.8f, 0.4f, 1f));
        private static readonly uint ColFace = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 0.9f, 1f));
        private static readonly uint ColSelected = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.4f, 0.2f, 1f));

        // ══════════════════════════ 构造 ══════════════════════════

        public ModelingPanel() : base("建模工具", "Modeling")
        {
            CanClose = true;
            MinSize = new Vector2(280, 400);
        }

        // ══════════════════════════ 主绘制 ══════════════════════════

        protected override void OnDraw(EditorContext ctx)
        {
            DrawPrimitiveGenerator();
            ImGui.Separator();

            DrawEditModeSelector();
            ImGui.Separator();

            switch (_editMode)
            {
                case MeshEditMode.Vertex:
                case MeshEditMode.Edge:
                case MeshEditMode.Face:
                    DrawEditTools();
                    break;
                case MeshEditMode.Object:
                    DrawObjectTools();
                    break;
            }

            ImGui.Separator();
            DrawSelectionOptions();
            ImGui.Separator();
            DrawMeshStats();
            ImGui.Separator();
            DrawExportSection();
        }

        // ══════════════════════════ 基础体生成 ══════════════════════════

        private void DrawPrimitiveGenerator()
        {
            ImGui.Text("基础体");
            ImGui.Spacing();

            float w = ImGui.GetContentRegionAvail().X / 3f - 4f;

            if (ImGui.Button("立方体", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Cube;
            ImGui.SameLine();
            if (ImGui.Button("球体", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Sphere;
            ImGui.SameLine();
            if (ImGui.Button("圆柱", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Cylinder;

            if (ImGui.Button("平面", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Plane;
            ImGui.SameLine();
            if (ImGui.Button("圆锥", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Cone;
            ImGui.SameLine();
            if (ImGui.Button("圆环", new Vector2(w, 30)))
                _pendingPrimitive = PrimitiveType.Torus;

            if (_pendingPrimitive != PrimitiveType.None)
            {
                // 通知引擎创建基础体 (通过事件或命令系统)
                OnCreatePrimitive?.Invoke(_pendingPrimitive);
                _pendingPrimitive = PrimitiveType.None;
            }
        }

        // ══════════════════════════ 编辑模式选择 ══════════════════════════

        private void DrawEditModeSelector()
        {
            ImGui.Text("编辑模式");
            ImGui.Spacing();

            float w = ImGui.GetContentRegionAvail().X / 4f - 3f;

            // Object 模式
            if (_editMode == MeshEditMode.Object)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.35f, 1f));
            if (ImGui.Button("对象", new Vector2(w, 0))) _editMode = MeshEditMode.Object;
            if (_editMode == MeshEditMode.Object) ImGui.PopStyleColor();

            // Vertex 模式
            ImGui.SameLine();
            if (_editMode == MeshEditMode.Vertex)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.5f, 0.15f, 1f));
            if (ImGui.Button("顶点", new Vector2(w, 0))) _editMode = MeshEditMode.Vertex;
            if (_editMode == MeshEditMode.Vertex) ImGui.PopStyleColor();

            // Edge 模式
            ImGui.SameLine();
            if (_editMode == MeshEditMode.Edge)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.4f, 0.2f, 1f));
            if (ImGui.Button("边", new Vector2(w, 0))) _editMode = MeshEditMode.Edge;
            if (_editMode == MeshEditMode.Edge) ImGui.PopStyleColor();

            // Face 模式
            ImGui.SameLine();
            if (_editMode == MeshEditMode.Face)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.25f, 0.45f, 1f));
            if (ImGui.Button("面", new Vector2(w, 0))) _editMode = MeshEditMode.Face;
            if (_editMode == MeshEditMode.Face) ImGui.PopStyleColor();
        }

        // ══════════════════════════ 编辑工具 ══════════════════════════

        private void DrawEditTools()
        {
            ImGui.Text("编辑工具");
            ImGui.Spacing();

            // 挤出
            if (ImGui.CollapsingHeader("挤出 Extrude"))
            {
                ImGui.SliderFloat("距离", ref _extrudeDistance, -10f, 10f, "%.2f");
                if (ImGui.Button("挤出选中面", new Vector2(-1, 24)))
                    _activeTool = ModelingTool.Extrude;
            }

            // 内缩
            if (ImGui.CollapsingHeader("内缩 Inset"))
            {
                ImGui.SliderFloat("量", ref _insetAmount, 0.01f, 1f, "%.3f");
                if (ImGui.Button("内缩选中面", new Vector2(-1, 24)))
                    _activeTool = ModelingTool.Inset;
            }

            // 倒角
            if (ImGui.CollapsingHeader("倒角 Bevel"))
            {
                ImGui.SliderFloat("宽度", ref _bevelWidth, 0.01f, 5f, "%.2f");
                ImGui.SliderInt("段数", ref _bevelSegments, 1, 10);
                // _bevelSegments is now int
                if (ImGui.Button("倒角选中边/面", new Vector2(-1, 24)))
                    _activeTool = ModelingTool.Bevel;
            }

            // 环切
            if (ImGui.CollapsingHeader("环切 Loop Cut"))
            {
                int pos = _loopCutPosition;
                ImGui.SliderInt("位置", ref pos, 0, 100, "%d%%");
                _loopCutPosition = pos;
                if (ImGui.Button("执行环切", new Vector2(-1, 24)))
                    _activeTool = ModelingTool.LoopCut;
            }

            // 其他操作
            if (ImGui.CollapsingHeader("其他操作"))
            {
                if (ImGui.Button("桥接", new Vector2(-1, 22))) _activeTool = ModelingTool.Bridge;
                if (ImGui.Button("合并顶点", new Vector2(-1, 22))) _activeTool = ModelingTool.Merge;
                if (ImGui.Button("分割面", new Vector2(-1, 22))) _activeTool = ModelingTool.Split;
                if (ImGui.Button("删除选中", new Vector2(-1, 22))) _activeTool = ModelingTool.Delete;
                if (ImGui.Button("复制", new Vector2(-1, 22))) _activeTool = ModelingTool.Duplicate;
            }
        }

        // ══════════════════════════ 对象工具 ══════════════════════════

        private void DrawObjectTools()
        {
            ImGui.Text("对象工具");
            ImGui.Spacing();

            if (ImGui.Button("翻转法线", new Vector2(-1, 24)))
                _activeTool = ModelingTool.FlipNormals;

            if (ImGui.Button("重计算法线", new Vector2(-1, 24)))
                _activeTool = ModelingTool.RecalculateNormals;

            if (ImGui.Button("平滑法线", new Vector2(-1, 24)))
                _activeTool = ModelingTool.SmoothNormals;

            ImGui.Separator();

            // UV 工具
            if (ImGui.CollapsingHeader("UV 工具"))
            {
                ImGui.InputInt("UV 通道", ref _uvChannel, 1, 1);
                ImGui.SliderFloat("UV 缩放", ref _uvScale, 0.1f, 10f);

                if (ImGui.Button("自动展开", new Vector2(-1, 22)))
                    _activeTool = ModelingTool.AutoUnwrap;

                if (ImGui.Button("平面投影 UV", new Vector2(-1, 22)))
                    _activeTool = ModelingTool.PlanarUV;
            }

            // 网格验证
            if (ImGui.CollapsingHeader("网格验证"))
            {
                if (ImGui.Button("检查流形", new Vector2(-1, 22)))
                    _activeTool = ModelingTool.CheckManifold;

                if (ImGui.Button("查找孤立顶点", new Vector2(-1, 22)))
                    _activeTool = ModelingTool.FindOrphan;

                if (ImGui.Button("合并重叠顶点", new Vector2(-1, 22)))
                    _activeTool = ModelingTool.MergeDuplicate;
            }
        }

        // ══════════════════════════ 选择选项 ══════════════════════════

        private void DrawSelectionOptions()
        {
            ImGui.Text("选择选项");
            ImGui.Spacing();

            ImGui.Checkbox("多选 (Ctrl)", ref _selectMultiple);
            ImGui.Checkbox("软选择", ref _softSelect);

            if (_softSelect)
            {
                ImGui.SliderFloat("软选择半径", ref _softSelectRadius, 0.1f, 20f);
            }

            ImGui.Separator();
            ImGui.Text("全选/取消");
            float w = ImGui.GetContentRegionAvail().X / 3f - 3f;
            if (ImGui.Button("全选", new Vector2(w, 22))) { }
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(w, 22))) { }
            ImGui.SameLine();
            if (ImGui.Button("反选", new Vector2(w, 22))) { }
        }

        // ══════════════════════════ 网格统计 ══════════════════════════

        private void DrawMeshStats()
        {
            ImGui.Text("网格统计");
            ImGui.Spacing();

            ImGui.Columns(2, "meshstats", false);
            ImGui.Text("顶点"); ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.3f, 1f), $"{_vertexCount}");
            ImGui.NextColumn();

            ImGui.Text("边"); ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.4f, 1f), $"{_edgeCount}");
            ImGui.NextColumn();

            ImGui.Text("面"); ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.3f, 0.5f, 0.9f, 1f), $"{_faceCount}");
            ImGui.NextColumn();

            ImGui.Text("已选顶点"); ImGui.NextColumn();
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.2f, 1f), $"{_selectedVertCount}");
            ImGui.NextColumn();
            ImGui.Columns(1);
        }

        // ══════════════════════════ 导出 ══════════════════════════

        private void DrawExportSection()
        {
            ImGui.Text("导出模型");
            ImGui.Spacing();

            float w = ImGui.GetContentRegionAvail().X / 3f - 4f;

            if (ImGui.Button("FBX", new Vector2(w, 24))) { }
            ImGui.SameLine();
            if (ImGui.Button("OBJ", new Vector2(w, 24))) { }
            ImGui.SameLine();
            if (ImGui.Button("glTF", new Vector2(w, 24))) { }
        }

        // ── 公共属性 ──────────────────────────────────────────────────────

        /// <summary>请求创建基础体事件。</summary>
        public event Action<PrimitiveType> OnCreatePrimitive;

        /// <summary>当前编辑模式。</summary>
        public MeshEditMode EditMode => _editMode;

        /// <summary>当前活动工具。</summary>
        public ModelingTool ActiveTool => _activeTool;

        /// <summary>更新网格统计。</summary>
        public void UpdateStats(int verts, int edges, int faces, int selVerts)
        {
            _vertexCount = verts;
            _edgeCount = edges;
            _faceCount = faces;
            _selectedVertCount = selVerts;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 枚举
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>网格编辑模式。</summary>
    public enum MeshEditMode
    {
        Object,
        Vertex,
        Edge,
        Face
    }

    /// <summary>基础体类型。</summary>
    public enum PrimitiveType
    {
        None,
        Cube,
        Sphere,
        Cylinder,
        Plane,
        Cone,
        Torus
    }

    /// <summary>建模工具操作类型。</summary>
    public enum ModelingTool
    {
        None,
        Extrude,
        Inset,
        Bevel,
        LoopCut,
        Bridge,
        Merge,
        Split,
        Delete,
        Duplicate,
        FlipNormals,
        RecalculateNormals,
        SmoothNormals,
        AutoUnwrap,
        PlanarUV,
        CheckManifold,
        FindOrphan,
        MergeDuplicate
    }
}
