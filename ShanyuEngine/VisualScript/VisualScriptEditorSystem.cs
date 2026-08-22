using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 · 可视化脚本编辑器系统 (Visual Script Editor System)
    //  ────────────────────────────────────────────────────────────────────────────
    //  提供完整的可视化脚本编辑器数据模型和交互系统：
    //    · EditorPreferences       — 编辑器偏好设置
    //    · NodeCanvas              — 节点画布（平移/缩放/框选/网格/小地图）
    //    · ConnectionRenderer      — 连线渲染配置（贝塞尔/直线/阶梯/颜色/动画）
    //    · NodeCommentBox          — 注释框
    //    · NodeGroup               — 节点分组
    //    · NodeContextMenu         — 右键菜单
    //    · NodeSearchPopup         — 节点搜索弹窗
    //    · EditorHistory           — 撤销/重做系统
    //    · NodeAutoLayout          — 节点自动布局
    //    · VisualScriptEditor      — 编辑器主系统（整合以上所有）
    //  作者：山雨引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  EditorPreferences - 编辑器偏好设置
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 连线渲染风格。
    /// </summary>
    public enum ConnectionStyle
    {
        /// <summary>贝塞尔曲线。</summary>
        Bezier,
        /// <summary>直线。</summary>
        Straight,
        /// <summary>阶梯折线。</summary>
        Step,
        /// <summary>正交折线。</summary>
        Orthogonal
    }

    /// <summary>
    /// 编辑器主题。
    /// </summary>
    public enum EditorTheme
    {
        /// <summary>暗色主题。</summary>
        Dark,
        /// <summary>亮色主题。</summary>
        Light,
        /// <summary>蓝黑主题（类 VS Code Dark+）。</summary>
        BlueDark,
        /// <summary>自定义主题。</summary>
        Custom
    }

    /// <summary>
    /// 网格显示模式。
    /// </summary>
    public enum GridDisplayMode
    {
        /// <summary>始终显示网格。</summary>
        Always,
        /// <summary>仅缩放时显示。</summary>
        OnZoom,
        /// <summary>不显示。</summary>
        Never
    }

    /// <summary>
    /// 编辑器偏好设置：控制编辑器外观和行为。
    /// </summary>
    [Serializable]
    public class EditorPreferences
    {
        /// <summary>网格大小（像素）。</summary>
        public float GridSize { get; set; } = 20f;

        /// <summary>辅助网格大小（细分网格）。</summary>
        public float SubGridSize { get; set; } = 5f;

        /// <summary>连线渲染风格。</summary>
        public ConnectionStyle ConnectionStyle { get; set; } = ConnectionStyle.Bezier;

        /// <summary>贝塞尔曲线张力（0-1）。</summary>
        public float BezierTension { get; set; } = 0.5f;

        /// <summary>连线粗细。</summary>
        public float ConnectionThickness { get; set; } = 2f;

        /// <summary>编辑器主题。</summary>
        public EditorTheme Theme { get; set; } = EditorTheme.Dark;

        /// <summary>网格显示模式。</summary>
        public GridDisplayMode GridDisplayMode { get; set; } = GridDisplayMode.Always;

        /// <summary>是否显示对齐辅助线。</summary>
        public bool ShowAlignmentGuides { get; set; } = true;

        /// <summary>是否启用节点吸附到网格。</summary>
        public bool SnapToGrid { get; set; } = true;

        /// <summary>吸附网格大小。</summary>
        public float SnapGridSize { get; set; } = 10f;

        /// <summary>是否显示小地图。</summary>
        public bool ShowMinimap { get; set; } = true;

        /// <summary>小地图位置（0=左下, 1=右下, 2=右上, 3=左上）。</summary>
        public int MinimapPosition { get; set; } = 1;

        /// <summary>小地图大小比例。</summary>
        public float MinimapScale { get; set; } = 0.15f;

        /// <summary>是否显示连线动画流动效果。</summary>
        public bool ShowConnectionFlowAnimation { get; set; } = true;

        /// <summary>连线流动动画速度。</summary>
        public float ConnectionFlowSpeed { get; set; } = 1f;

        /// <summary>是否显示断裂线（悬空的连接）。</summary>
        public bool ShowBrokenConnections { get; set; } = true;

        /// <summary>节点选中高亮颜色。</summary>
        public string SelectionHighlightColor { get; set; } = "#00AAFF";

        /// <summary>节点悬停高亮颜色。</summary>
        public string HoverHighlightColor { get; set; } = "#66CCFF";

        /// <summary>画布背景颜色。</summary>
        public string CanvasBackgroundColor { get; set; } = "#1A1A2E";

        /// <summary>网格线颜色。</summary>
        public string GridLineColor { get; set; } = "#2A2A4A";

        /// <summary>辅助网格线颜色。</summary>
        public string SubGridLineColor { get; set; } = "#22223A";

        /// <summary>快捷键映射（动作名 → 快捷键字符串，如 "Ctrl+Z"）。</summary>
        public Dictionary<string, string> KeyBindings { get; set; } = new Dictionary<string, string>
        {
            { "Delete", "Delete" },
            { "Copy", "Ctrl+C" },
            { "Paste", "Ctrl+V" },
            { "Undo", "Ctrl+Z" },
            { "Redo", "Ctrl+Y" },
            { "SelectAll", "Ctrl+A" },
            { "DeselectAll", "Escape" },
            { "SearchNode", "RightClick" },
            { "QuickAdd", "DoubleClick" },
            { "ZoomIn", "Ctrl+=" },
            { "ZoomOut", "Ctrl+-" },
            { "FitView", "Home" },
            { "AlignLeft", "Alt+A" },
            { "AlignRight", "Alt+D" },
            { "AlignTop", "Alt+W" },
            { "AlignBottom", "Alt+S" },
            { "Compile", "Ctrl+Shift+B" },
            { "Save", "Ctrl+S" }
        };

        /// <summary>自动保存间隔（秒，0=禁用）。</summary>
        public int AutoSaveIntervalSeconds { get; set; } = 300;

        /// <summary>最大撤销步数。</summary>
        public int MaxUndoSteps { get; set; } = 100;

        /// <summary>编辑器字体大小。</summary>
        public float FontSize { get; set; } = 12f;

        /// <summary>节点标题字体大小。</summary>
        public float NodeTitleFontSize { get; set; } = 13f;

        /// <summary>节点引脚字体大小。</summary>
        public float PinFontSize { get; set; } = 11f;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeCanvas - 节点画布
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点画布：可视化管理画布的平移、缩放、框选、网格背景、对齐辅助线和小地图。
    /// </summary>
    [Serializable]
    public class NodeCanvas
    {
        /// <summary>画布平移偏移量。</summary>
        public Vector2 PanOffset { get; set; }

        /// <summary>画布缩放级别（0.1 - 5.0）。</summary>
        public float ZoomLevel { get; set; } = 1f;

        /// <summary>画布宽度。</summary>
        public float Width { get; set; } = 1920f;

        /// <summary>画布高度。</summary>
        public float Height { get; set; } = 1080f;

        /// <summary>最小缩放级别。</summary>
        public float MinZoom { get; set; } = 0.1f;

        /// <summary>最大缩放级别。</summary>
        public float MaxZoom { get; set; } = 5f;

        /// <summary>缩放步进值。</summary>
        public float ZoomStep { get; set; } = 0.1f;

        /// <summary>框选起始位置（画布坐标）。</summary>
        public Vector2? SelectionStart { get; set; }

        /// <summary>框选结束位置（画布坐标）。</summary>
        public Vector2? SelectionEnd { get; set; }

        /// <summary>是否正在框选。</summary>
        public bool IsSelecting { get; set; }

        /// <summary>当前选中的节点 ID 列表。</summary>
        public HashSet<Guid> SelectedNodeIds { get; set; } = new HashSet<Guid>();

        /// <summary>当前选中的连接 ID 列表。</summary>
        public HashSet<Guid> SelectedConnectionIds { get; set; } = new HashSet<Guid>();

        /// <summary>悬停的节点 ID。</summary>
        public Guid? HoveredNodeId { get; set; }

        /// <summary>悬停的引脚 ID。</summary>
        public Guid? HoveredPinId { get; set; }

        /// <summary>是否正在拖拽节点。</summary>
        public bool IsDraggingNodes { get; set; }

        /// <summary>是否正在平移画布。</summary>
        public bool IsPanning { get; set; }

        /// <summary>是否正在连接引脚。</summary>
        public bool IsConnecting { get; set; }

        /// <summary>连接源引脚 ID。</summary>
        public Guid? ConnectingSourcePinId { get; set; }

        /// <summary>当前鼠标画布坐标。</summary>
        public Vector2 MouseCanvasPosition { get; set; }

        /// <summary>对齐辅助线（水平线位置列表）。</summary>
        public List<float> HorizontalGuides { get; set; } = new List<float>();

        /// <summary>对齐辅助线（垂直线位置列表）。</summary>
        public List<float> VerticalGuides { get; set; } = new List<float>();

        /// <summary>
        /// 缩放到指定级别。
        /// </summary>
        public void ZoomTo(float level, Vector2? center = null)
        {
            ZoomLevel = Math.Clamp(level, MinZoom, MaxZoom);
        }

        /// <summary>
        /// 放大。
        /// </summary>
        public void ZoomIn() => ZoomTo(ZoomLevel + ZoomStep);

        /// <summary>
        /// 缩小。
        /// </summary>
        public void ZoomOut() => ZoomTo(ZoomLevel - ZoomStep);

        /// <summary>
        /// 平移画布。
        /// </summary>
        public void Pan(Vector2 delta)
        {
            PanOffset += delta / ZoomLevel;
        }

        /// <summary>
        /// 屏幕坐标转画布坐标。
        /// </summary>
        public Vector2 ScreenToCanvas(Vector2 screenPos)
        {
            return (screenPos - new Vector2(Width / 2, Height / 2)) / ZoomLevel - PanOffset;
        }

        /// <summary>
        /// 画布坐标转屏幕坐标。
        /// </summary>
        public Vector2 CanvasToScreen(Vector2 canvasPos)
        {
            return (canvasPos + PanOffset) * ZoomLevel + new Vector2(Width / 2, Height / 2);
        }

        /// <summary>
        /// 开始框选。
        /// </summary>
        public void BeginSelection(Vector2 canvasPos)
        {
            SelectionStart = canvasPos;
            SelectionEnd = canvasPos;
            IsSelecting = true;
        }

        /// <summary>
        /// 更新框选区域。
        /// </summary>
        public void UpdateSelection(Vector2 canvasPos)
        {
            if (!IsSelecting) return;
            SelectionEnd = canvasPos;
        }

        /// <summary>
        /// 结束框选并返回框选矩形内的节点列表。
        /// </summary>
        public List<Guid> EndSelection(NodeGraph graph)
        {
            IsSelecting = false;
            if (SelectionStart == null || SelectionEnd == null) return new List<Guid>();

            var min = Vector2.Min(SelectionStart.Value, SelectionEnd.Value);
            var max = Vector2.Max(SelectionStart.Value, SelectionEnd.Value);

            var selected = new List<Guid>();
            foreach (var node in graph.Nodes)
            {
                var nodeTopLeft = node.Position;
                var nodeBottomRight = node.Position + node.Size;

                if (nodeTopLeft.X >= min.X && nodeTopLeft.Y >= min.Y &&
                    nodeBottomRight.X <= max.X && nodeBottomRight.Y <= max.Y)
                {
                    selected.Add(node.Id);
                }
            }

            SelectedNodeIds = new HashSet<Guid>(selected);
            SelectionStart = null;
            SelectionEnd = null;
            return selected;
        }

        /// <summary>
        /// 选中一个节点（如果 Shift 未按下则清除其他选择）。
        /// </summary>
        public void SelectNode(Guid nodeId, bool addToSelection = false)
        {
            if (!addToSelection)
            {
                SelectedNodeIds.Clear();
                SelectedConnectionIds.Clear();
            }
            SelectedNodeIds.Add(nodeId);
        }

        /// <summary>
        /// 取消选择所有节点和连接。
        /// </summary>
        public void DeselectAll()
        {
            SelectedNodeIds.Clear();
            SelectedConnectionIds.Clear();
            HoveredNodeId = null;
            HoveredPinId = null;
        }

        /// <summary>
        /// 计算对齐辅助线。
        /// </summary>
        public void CalculateAlignmentGuides(NodeGraph graph, Vector2 draggingPosition)
        {
            HorizontalGuides.Clear();
            VerticalGuides.Clear();

            foreach (var node in graph.Nodes)
            {
                if (SelectedNodeIds.Contains(node.Id)) continue;

                // X 轴对齐
                if (Math.Abs(node.Position.X - draggingPosition.X) < 5)
                {
                    VerticalGuides.Add(node.Position.X);
                }
                if (Math.Abs(node.Position.X + node.Size.X / 2 - draggingPosition.X) < 5)
                {
                    VerticalGuides.Add(node.Position.X + node.Size.X / 2);
                }

                // Y 轴对齐
                if (Math.Abs(node.Position.Y - draggingPosition.Y) < 5)
                {
                    HorizontalGuides.Add(node.Position.Y);
                }
                if (Math.Abs(node.Position.Y + node.Size.Y / 2 - draggingPosition.Y) < 5)
                {
                    HorizontalGuides.Add(node.Position.Y + node.Size.Y / 2);
                }
            }
        }

        /// <summary>
        /// 调整视图以容纳所有节点。
        /// </summary>
        public void FitView(NodeGraph graph, float padding = 50)
        {
            if (graph.Nodes.Count == 0) return;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var node in graph.Nodes)
            {
                minX = Math.Min(minX, node.Position.X);
                minY = Math.Min(minY, node.Position.Y);
                maxX = Math.Max(maxX, node.Position.X + node.Size.X);
                maxY = Math.Max(maxY, node.Position.Y + node.Size.Y);
            }

            float contentWidth = maxX - minX + padding * 2;
            float contentHeight = maxY - minY + padding * 2;

            float scaleX = Width / contentWidth;
            float scaleY = Height / contentHeight;
            ZoomLevel = Math.Clamp(Math.Min(scaleX, scaleY), MinZoom, MaxZoom);

            var center = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
            PanOffset = -center / ZoomLevel;
        }

        /// <summary>
        /// 获取小地图渲染数据。
        /// </summary>
        public (Vector2 minimapOffset, float minimapZoom, List<(Guid id, Vector2 pos, Vector2 size, string color)> nodes)
            GetMinimapData(NodeGraph graph)
        {
            if (graph.Nodes.Count == 0)
                return (Vector2.Zero, 1f, new List<(Guid, Vector2, Vector2, string)>());

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var node in graph.Nodes)
            {
                minX = Math.Min(minX, node.Position.X);
                minY = Math.Min(minY, node.Position.Y);
                maxX = Math.Max(maxX, node.Position.X + node.Size.X);
                maxY = Math.Max(maxY, node.Position.Y + node.Size.Y);
            }

            float contentWidth = maxX - minX;
            float contentHeight = maxY - minY;

            float minimapWidth = Width * 0.15f;
            float minimapHeight = Height * 0.15f;

            float mScale = Math.Min(minimapWidth / contentWidth, minimapHeight / contentHeight);

            var nodes = graph.Nodes.Select(n => (n.Id, n.Position, n.Size, n.ColorHex)).ToList();

            return (new Vector2(minX, minY), mScale, nodes);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ConnectionRenderer - 连线渲染配置
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 连线渲染器：配置和管理连线的视觉表现。
    /// </summary>
    [Serializable]
    public class ConnectionRenderer
    {
        /// <summary>连线风格。</summary>
        public ConnectionStyle Style { get; set; } = ConnectionStyle.Bezier;

        /// <summary>贝塞尔曲线张力。</summary>
        public float BezierTension { get; set; } = 0.5f;

        /// <summary>连线默认粗细。</summary>
        public float DefaultThickness { get; set; } = 2f;

        /// <summary>选中连线粗细。</summary>
        public float SelectedThickness { get; set; } = 3f;

        /// <summary>是否显示流动动画。</summary>
        public bool ShowFlowAnimation { get; set; } = true;

        /// <summary>流动动画速度。</summary>
        public float FlowSpeed { get; set; } = 1f;

        /// <summary>是否显示断裂连接标记。</summary>
        public bool ShowBrokenConnections { get; set; } = true;

        /// <summary>断裂连接颜色。</summary>
        public string BrokenConnectionColor { get; set; } = "#FF0000";

        /// <summary>断裂连接虚线间隔。</summary>
        public float BrokenDashInterval { get; set; } = 5f;

        /// <summary>连接颜色编码（按数据类型）。</summary>
        public Dictionary<GraphPinDataType, string> DataTypeColors { get; set; } = new Dictionary<GraphPinDataType, string>
        {
            { GraphPinDataType.Exec, "#FFFFFF" },
            { GraphPinDataType.Bool, "#CC0000" },
            { GraphPinDataType.Int, "#00CCCC" },
            { GraphPinDataType.Float, "#00CC66" },
            { GraphPinDataType.Double, "#00FF66" },
            { GraphPinDataType.String, "#FF00FF" },
            { GraphPinDataType.Vector3, "#FFCC00" },
            { GraphPinDataType.Rotator, "#9966FF" },
            { GraphPinDataType.Transform, "#CC6600" },
            { GraphPinDataType.Object, "#0000CC" },
            { GraphPinDataType.Array, "#FFFFFF" },
            { GraphPinDataType.Wildcard, "#888888" }
        };

        /// <summary>
        /// 获取指定数据类型的连线颜色。
        /// </summary>
        public string GetConnectionColor(GraphPinDataType dataType)
        {
            return DataTypeColors.TryGetValue(dataType, out var color) ? color : "#FFFFFF";
        }

        /// <summary>
        /// 计算贝塞尔曲线控制点。
        /// </summary>
        public (Vector2 cp1, Vector2 cp2) GetBezierControlPoints(Vector2 start, Vector2 end)
        {
            float dx = Math.Abs(end.X - start.X) * BezierTension;
            return (new Vector2(start.X + dx, start.Y), new Vector2(end.X - dx, end.Y));
        }

        /// <summary>
        /// 计算阶梯线的中间点。
        /// </summary>
        public Vector2[] GetStepPoints(Vector2 start, Vector2 end)
        {
            float midX = (start.X + end.X) / 2;
            return new[]
            {
                start,
                new Vector2(midX, start.Y),
                new Vector2(midX, end.Y),
                end
            };
        }

        /// <summary>
        /// 计算正交折线点。
        /// </summary>
        public Vector2[] GetOrthogonalPoints(Vector2 start, Vector2 end)
        {
            var offset = Math.Abs(end.X - start.X) * 0.5f;
            return new[]
            {
                start,
                new Vector2(start.X + offset, start.Y),
                new Vector2(end.X - offset, end.Y),
                end
            };
        }

        /// <summary>
        /// 获取流动动画偏移（用于渲染时随时间更新）。
        /// </summary>
        public float GetFlowOffset(float time)
        {
            return (time * FlowSpeed) % 20f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeCommentBox - 注释框
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点注释框：用于对一组节点添加注释说明。
    /// </summary>
    [Serializable]
    public class NodeCommentBox
    {
        /// <summary>注释框唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>注释标题。</summary>
        public string Title { get; set; } = "Comment";

        /// <summary>注释内容。</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>注释框位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>注释框大小。</summary>
        public Vector2 Size { get; set; } = new Vector2(300, 200);

        /// <summary>背景颜色。</summary>
        public string ColorHex { get; set; } = "#33335580";

        /// <summary>字体颜色。</summary>
        public string FontColorHex { get; set; } = "#FFFFFF";

        /// <summary>关联的节点 ID 列表。</summary>
        public List<Guid> AssociatedNodeIds { get; set; } = new List<Guid>();

        /// <summary>是否锁定（不可编辑）。</summary>
        public bool IsLocked { get; set; }

        /// <summary>字体大小。</summary>
        public float FontSize { get; set; } = 12f;

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>是否被折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>Z 排序层级（越大越在前）。</summary>
        public int ZOrder { get; set; }

        /// <summary>
        /// 将指定节点添加到注释框中。
        /// </summary>
        public void AddNode(Guid nodeId)
        {
            if (!AssociatedNodeIds.Contains(nodeId))
            {
                AssociatedNodeIds.Add(nodeId);
            }
        }

        /// <summary>
        /// 从注释框中移除指定节点。
        /// </summary>
        public void RemoveNode(Guid nodeId)
        {
            AssociatedNodeIds.Remove(nodeId);
        }

        /// <summary>
        /// 检查一个点是否在注释框内。
        /// </summary>
        public bool ContainsPoint(Vector2 point)
        {
            return point.X >= Position.X && point.X <= Position.X + Size.X &&
                   point.Y >= Position.Y && point.Y <= Position.Y + Size.Y;
        }

        /// <summary>
        /// 自动调整注释框大小以包含所有关联节点。
        /// </summary>
        public void AutoFitToNodes(NodeGraph graph)
        {
            if (AssociatedNodeIds.Count == 0) return;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var nodeId in AssociatedNodeIds)
            {
                var node = graph.GetNode(nodeId);
                if (node == null) continue;

                minX = Math.Min(minX, node.Position.X);
                minY = Math.Min(minY, node.Position.Y);
                maxX = Math.Max(maxX, node.Position.X + node.Size.X);
                maxY = Math.Max(maxY, node.Position.Y + node.Size.Y);
            }

            float padding = 20;
            Position = new Vector2(minX - padding, minY - padding - 30);
            Size = new Vector2(maxX - minX + padding * 2, maxY - minY + padding * 2 + 30);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeGroup - 节点分组
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点分组：按功能将节点分组，支持颜色标记和折叠。
    /// </summary>
    [Serializable]
    public class NodeGroup
    {
        /// <summary>分组唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>分组名称。</summary>
        public string Name { get; set; } = "Group";

        /// <summary>分组描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>分组颜色。</summary>
        public string ColorHex { get; set; } = "#33557780";

        /// <summary>分组中的节点 ID 列表。</summary>
        public List<Guid> NodeIds { get; set; } = new List<Guid>();

        /// <summary>是否折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>折叠时的替代显示大小。</summary>
        public Vector2 CollapsedSize { get; set; } = new Vector2(150, 40);

        /// <summary>Z 排序层级。</summary>
        public int ZOrder { get; set; }

        /// <summary>是否锁定。</summary>
        public bool IsLocked { get; set; }

        /// <summary>
        /// 添加节点到分组。
        /// </summary>
        public void AddNode(Guid nodeId)
        {
            if (!NodeIds.Contains(nodeId))
            {
                NodeIds.Add(nodeId);
            }
        }

        /// <summary>
        /// 从分组移除节点。
        /// </summary>
        public void RemoveNode(Guid nodeId)
        {
            NodeIds.Remove(nodeId);
        }

        /// <summary>
        /// 切换折叠状态。
        /// </summary>
        public void ToggleCollapse()
        {
            IsCollapsed = !IsCollapsed;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeContextMenu - 右键菜单
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点右键菜单项。
    /// </summary>
    [Serializable]
    public class ContextMenuItem
    {
        /// <summary>菜单项唯一标识符。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>菜单项显示文本。</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>菜单项图标名称（可选）。</summary>
        public string? Icon { get; set; }

        /// <summary>菜单项快捷键提示文本。</summary>
        public string? ShortcutHint { get; set; }

        /// <summary>子菜单项。</summary>
        public List<ContextMenuItem> Children { get; set; } = new List<ContextMenuItem>();

        /// <summary>菜单项动作回调。</summary>
        public Action? OnClick { get; set; }

        /// <summary>菜单项是否可用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>菜单项是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>是否为分隔线。</summary>
        public bool IsSeparator { get; set; }

        /// <summary>是否被选中（复选状态）。</summary>
        public bool IsChecked { get; set; }

        /// <summary>这是否是一个子菜单。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasChildren => Children.Count > 0;
    }

    /// <summary>
    /// 节点右键菜单：提供快速添加节点、编辑操作等功能。
    /// </summary>
    [Serializable]
    public class NodeContextMenu
    {
        /// <summary>菜单是否可见。</summary>
        public bool IsVisible { get; set; }

        /// <summary>菜单位置（画布坐标）。</summary>
        public Vector2 Position { get; set; }

        /// <summary>根菜单项列表。</summary>
        public List<ContextMenuItem> RootItems { get; set; } = new List<ContextMenuItem>();

        /// <summary>最近使用的节点类型列表。</summary>
        public List<string> RecentlyUsedNodes { get; set; } = new List<string>();

        /// <summary>收藏的节点类型列表。</summary>
        public List<string> FavoriteNodes { get; set; } = new List<string>();

        /// <summary>最近使用的最大记录数。</summary>
        public int MaxRecentNodes { get; set; } = 10;

        /// <summary>
        /// 记录使用过的节点类型。
        /// </summary>
        public void AddRecentNode(string nodeType)
        {
            RecentlyUsedNodes.Remove(nodeType);
            RecentlyUsedNodes.Insert(0, nodeType);
            if (RecentlyUsedNodes.Count > MaxRecentNodes)
            {
                RecentlyUsedNodes.RemoveAt(RecentlyUsedNodes.Count - 1);
            }
        }

        /// <summary>
        /// 构建默认右键菜单。
        /// </summary>
        public void BuildDefaultMenu(VisualScriptEditor editor)
        {
            RootItems.Clear();

            // 添加节点（按分类）
            var addNodeItem = new ContextMenuItem { Id = "AddNode", Label = "Add Node" };
            foreach (NodeCategoryType cat in Enum.GetValues(typeof(NodeCategoryType)))
            {
                var catItem = new ContextMenuItem
                {
                    Id = $"Category_{cat}",
                    Label = cat.ToString()
                };
                var nodes = NodeScriptNodeRegistry.GetNodesByCategory(cat);
                foreach (var desc in nodes)
                {
                    catItem.Children.Add(new ContextMenuItem
                    {
                        Id = $"Node_{desc.NodeType}",
                        Label = desc.Title,
                        OnClick = () => editor.AddNodeAtPosition(desc.NodeType, Position)
                    });
                }
                addNodeItem.Children.Add(catItem);
            }
            RootItems.Add(addNodeItem);

            RootItems.Add(new ContextMenuItem { IsSeparator = true });

            // 编辑操作
            RootItems.Add(new ContextMenuItem
            {
                Id = "Copy", Label = "Copy", ShortcutHint = "Ctrl+C",
                OnClick = () => editor.CopySelectedNodes()
            });
            RootItems.Add(new ContextMenuItem
            {
                Id = "Paste", Label = "Paste", ShortcutHint = "Ctrl+V",
                OnClick = () => editor.PasteNodes(Position)
            });
            RootItems.Add(new ContextMenuItem
            {
                Id = "Delete", Label = "Delete", ShortcutHint = "Delete",
                OnClick = () => editor.DeleteSelected()
            });
            RootItems.Add(new ContextMenuItem
            {
                Id = "Duplicate", Label = "Duplicate",
                OnClick = () => editor.DuplicateSelected()
            });

            RootItems.Add(new ContextMenuItem { IsSeparator = true });

            // 注释
            RootItems.Add(new ContextMenuItem
            {
                Id = "AddComment", Label = "Add Comment",
                OnClick = () => editor.AddCommentBox(Position)
            });

            // 最近使用
            if (RecentlyUsedNodes.Count > 0)
            {
                RootItems.Add(new ContextMenuItem { IsSeparator = true });
                var recentItem = new ContextMenuItem { Id = "Recent", Label = "Recent" };
                foreach (var nodeType in RecentlyUsedNodes)
                {
                    var desc = NodeScriptNodeRegistry.GetNodeDescriptor(nodeType);
                    if (desc != null)
                    {
                        recentItem.Children.Add(new ContextMenuItem
                        {
                            Id = $"Recent_{nodeType}",
                            Label = desc.Title,
                            OnClick = () => editor.AddNodeAtPosition(nodeType, Position)
                        });
                    }
                }
                RootItems.Add(recentItem);
            }
        }

        /// <summary>
        /// 显示菜单。
        /// </summary>
        public void Show(Vector2 position, VisualScriptEditor editor)
        {
            BuildDefaultMenu(editor);
            Position = position;
            IsVisible = true;
        }

        /// <summary>
        /// 隐藏菜单。
        /// </summary>
        public void Hide()
        {
            IsVisible = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeSearchPopup - 节点搜索弹窗
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 节点搜索结果条目。
    /// </summary>
    [Serializable]
    public class NodeSearchResult
    {
        /// <summary>节点类型。</summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>节点标题。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>节点分类。</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>节点描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>节点颜色。</summary>
        public string ColorHex { get; set; } = "#808080";

        /// <summary>匹配分数（用于排序）。</summary>
        public float MatchScore { get; set; }
    }

    /// <summary>
    /// 节点搜索弹窗：提供模糊搜索、分类过滤、使用频率排序等功能。
    /// </summary>
    [Serializable]
    public class NodeSearchPopup
    {
        /// <summary>搜索弹窗是否可见。</summary>
        public bool IsVisible { get; set; }

        /// <summary>弹窗位置（屏幕坐标）。</summary>
        public Vector2 Position { get; set; }

        /// <summary>当前搜索关键词。</summary>
        public string SearchQuery { get; set; } = string.Empty;

        /// <summary>搜索结果列表。</summary>
        public List<NodeSearchResult> Results { get; set; } = new List<NodeSearchResult>();

        /// <summary>当前选中索引。</summary>
        public int SelectedIndex { get; set; } = -1;

        /// <summary>分类过滤器。</summary>
        public NodeCategoryType? CategoryFilter { get; set; }

        /// <summary>节点使用频率记录。</summary>
        public Dictionary<string, int> UsageFrequency { get; set; } = new Dictionary<string, int>();

        /// <summary>最近使用的节点列表。</summary>
        public List<string> RecentlyUsed { get; set; } = new List<string>();

        /// <summary>最大显示结果数。</summary>
        public int MaxDisplayResults { get; set; } = 20;

        /// <summary>弹窗宽度。</summary>
        public float PopupWidth { get; set; } = 350f;

        /// <summary>每项高度。</summary>
        public float ItemHeight { get; set; } = 30f;

        /// <summary>
        /// 执行搜索。
        /// </summary>
        public void Search()
        {
            Results.Clear();
            SelectedIndex = -1;

            if (string.IsNullOrEmpty(SearchQuery) && !CategoryFilter.HasValue)
            {
                // 无搜索词时显示最近使用
                foreach (var nodeType in RecentlyUsed)
                {
                    var desc = NodeScriptNodeRegistry.GetNodeDescriptor(nodeType);
                    if (desc != null)
                    {
                        Results.Add(new NodeSearchResult
                        {
                            NodeType = desc.NodeType,
                            Title = desc.Title,
                            Category = desc.Category.ToString(),
                            Description = desc.Description,
                            ColorHex = desc.ColorHex,
                            MatchScore = 1000
                        });
                    }
                }
                return;
            }

            var descriptors = NodeScriptNodeRegistry.SearchNodes(SearchQuery, CategoryFilter);

            foreach (var desc in descriptors)
            {
                var score = CalculateMatchScore(desc, SearchQuery);
                Results.Add(new NodeSearchResult
                {
                    NodeType = desc.NodeType,
                    Title = desc.Title,
                    Category = desc.SubCategory,
                    Description = desc.Description,
                    ColorHex = desc.ColorHex,
                    MatchScore = score
                });
            }

            // 按匹配分数降序排序
            Results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));

            // 限制显示数量
            if (Results.Count > MaxDisplayResults)
            {
                Results = Results.GetRange(0, MaxDisplayResults);
            }

            if (Results.Count > 0)
            {
                SelectedIndex = 0;
            }
        }

        /// <summary>
        /// 计算搜索匹配分数。
        /// </summary>
        private float CalculateMatchScore(NodeScriptNodeDescriptor desc, string query)
        {
            float score = 0;
            var lower = query.ToLowerInvariant();

            // 使用频率加分
            if (UsageFrequency.TryGetValue(desc.NodeType, out var freq))
            {
                score += Math.Min(freq * 2, 100);
            }

            // 标题完全匹配
            if (desc.Title.ToLowerInvariant() == lower) score += 500;
            else if (desc.Title.ToLowerInvariant().StartsWith(lower)) score += 300;
            else if (desc.Title.ToLowerInvariant().Contains(lower)) score += 100;

            // 类型名匹配
            if (desc.NodeType.ToLowerInvariant().Contains(lower)) score += 200;

            return score;
        }

        /// <summary>
        /// 选择下一个结果。
        /// </summary>
        public void SelectNext()
        {
            if (Results.Count == 0) return;
            SelectedIndex = (SelectedIndex + 1) % Results.Count;
        }

        /// <summary>
        /// 选择上一个结果。
        /// </summary>
        public void SelectPrevious()
        {
            if (Results.Count == 0) return;
            SelectedIndex = SelectedIndex <= 0 ? Results.Count - 1 : SelectedIndex - 1;
        }

        /// <summary>
        /// 确认选择当前项。
        /// </summary>
        public NodeSearchResult? ConfirmSelection()
        {
            if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return null;
            var result = Results[SelectedIndex];

            // 更新使用频率
            if (!UsageFrequency.ContainsKey(result.NodeType))
                UsageFrequency[result.NodeType] = 0;
            UsageFrequency[result.NodeType]++;

            // 更新最近使用
            RecentlyUsed.Remove(result.NodeType);
            RecentlyUsed.Insert(0, result.NodeType);
            if (RecentlyUsed.Count > 10)
                RecentlyUsed.RemoveAt(RecentlyUsed.Count - 1);

            return result;
        }

        /// <summary>
        /// 显示搜索弹窗。
        /// </summary>
        public void Show(Vector2 position)
        {
            Position = position;
            SearchQuery = string.Empty;
            CategoryFilter = null;
            IsVisible = true;
            Search();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  EditorHistory - 撤销/重做系统
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器历史操作记录。
    /// </summary>
    [Serializable]
    public class EditorAction
    {
        /// <summary>操作描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>操作类型。</summary>
        public string ActionType { get; set; } = string.Empty;

        /// <summary>操作前的图快照（JSON）。</summary>
        public string BeforeSnapshot { get; set; } = string.Empty;

        /// <summary>操作后的图快照（JSON）。</summary>
        public string AfterSnapshot { get; set; } = string.Empty;

        /// <summary>操作时间戳。</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 编辑器历史记录系统：支持撤销/重做。
    /// </summary>
    [Serializable]
    public class EditorHistory
    {
        /// <summary>撤销栈。</summary>
        private readonly Stack<EditorAction> _undoStack = new Stack<EditorAction>();

        /// <summary>重做栈。</summary>
        private readonly Stack<EditorAction> _redoStack = new Stack<EditorAction>();

        /// <summary>最大历史记录数。</summary>
        public int MaxHistorySize { get; set; } = 100;

        /// <summary>是否能撤销。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>是否能重做。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>撤销栈中的操作数量。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int UndoCount => _undoStack.Count;

        /// <summary>重做栈中的操作数量。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int RedoCount => _redoStack.Count;

        /// <summary>当前操作触发前记录图快照。</summary>
        public void BeginAction(string description, string actionType, NodeGraph graph)
        {
            var action = new EditorAction
            {
                Description = description,
                ActionType = actionType,
                BeforeSnapshot = graph.SerializeToJson()
            };

            // 用临时字段保存，在 EndAction 时获取 AfterSnapshot
            _undoStack.Push(action);
            _redoStack.Clear();
        }

        /// <summary>
        /// 当前操作完成后记录图快照。
        /// </summary>
        public void EndAction(NodeGraph graph)
        {
            if (_undoStack.Count == 0) return;
            var action = _undoStack.Peek();
            action.AfterSnapshot = graph.SerializeToJson();

            // 限制历史记录大小
            while (_undoStack.Count > MaxHistorySize)
            {
                var dummy = new Stack<EditorAction>();
                var items = _undoStack.ToArray();
                for (int i = 0; i < items.Length - MaxHistorySize; i++)
                    continue;
                for (int i = Math.Max(0, items.Length - MaxHistorySize); i < items.Length; i++)
                    dummy.Push(items[i]);
                _undoStack.Clear();
                foreach (var item in dummy) _undoStack.Push(item);
            }
        }

        /// <summary>
        /// 撤销最近一次操作。
        /// </summary>
        public bool Undo(NodeGraph graph)
        {
            if (_undoStack.Count == 0) return false;

            var action = _undoStack.Pop();
            var restored = NodeGraph.DeserializeFromJson(action.BeforeSnapshot);
            if (restored == null) return false;

            // 保存当前状态到重做栈
            var redoAction = new EditorAction
            {
                Description = action.Description,
                ActionType = action.ActionType,
                BeforeSnapshot = action.AfterSnapshot,
                AfterSnapshot = graph.SerializeToJson()
            };
            _redoStack.Push(redoAction);

            // 恢复图状态
            ApplySnapshot(graph, restored);
            return true;
        }

        /// <summary>
        /// 重做最近一次撤销的操作。
        /// </summary>
        public bool Redo(NodeGraph graph)
        {
            if (_redoStack.Count == 0) return false;

            var action = _redoStack.Pop();
            var restored = NodeGraph.DeserializeFromJson(action.AfterSnapshot);
            if (restored == null) return false;

            // 保存当前状态到撤销栈
            var undoAction = new EditorAction
            {
                Description = action.Description,
                ActionType = action.ActionType,
                BeforeSnapshot = graph.SerializeToJson(),
                AfterSnapshot = action.BeforeSnapshot
            };
            _undoStack.Push(undoAction);

            ApplySnapshot(graph, restored);
            return true;
        }

        /// <summary>
        /// 清空历史记录。
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        /// <summary>
        /// 将快照应用到图。
        /// </summary>
        private void ApplySnapshot(NodeGraph target, NodeGraph source)
        {
            target.Nodes.Clear();
            target.Connections.Clear();
            target.Variables.Clear();
            target.Events.Clear();
            target.Functions.Clear();

            foreach (var node in source.Nodes) target.Nodes.Add(node);
            foreach (var conn in source.Connections) target.Connections.Add(conn);
            foreach (var v in source.Variables) target.Variables.Add(v);
            foreach (var e in source.Events) target.Events.Add(e);
            foreach (var f in source.Functions) target.Functions.Add(f);

            target.Version = source.Version;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  NodeAutoLayout - 节点自动布局
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 自动布局算法类型。
    /// </summary>
    public enum AutoLayoutAlgorithm
    {
        /// <summary>按执行流层级排列。</summary>
        ExecutionFlow,
        /// <summary>按类型分组排列。</summary>
        CategoryGroup,
        /// <summary>按数据流方向排列。</summary>
        DataFlow,
        /// <summary>树形排列。</summary>
        Tree,
        /// <summary>力导向布局。</summary>
        ForceDirected
    }

    /// <summary>
    /// 节点自动布局：自动排列节点位置。
    /// </summary>
    public static class NodeAutoLayout
    {
        /// <summary>
        /// 执行自动布局。
        /// </summary>
        public static void Layout(NodeGraph graph, AutoLayoutAlgorithm algorithm, float horizontalSpacing = 300f, float verticalSpacing = 150f)
        {
            switch (algorithm)
            {
                case AutoLayoutAlgorithm.ExecutionFlow:
                    LayoutByExecutionFlow(graph, horizontalSpacing, verticalSpacing);
                    break;
                case AutoLayoutAlgorithm.CategoryGroup:
                    LayoutByCategory(graph, horizontalSpacing, verticalSpacing);
                    break;
                case AutoLayoutAlgorithm.Tree:
                    LayoutAsTree(graph, horizontalSpacing, verticalSpacing);
                    break;
                case AutoLayoutAlgorithm.DataFlow:
                    LayoutByDataFlow(graph, horizontalSpacing, verticalSpacing);
                    break;
                case AutoLayoutAlgorithm.ForceDirected:
                    LayoutForceDirected(graph);
                    break;
            }
        }

        /// <summary>
        /// 按执行流层级排列。
        /// </summary>
        private static void LayoutByExecutionFlow(NodeGraph graph, float hSpace, float vSpace)
        {
            var levels = ComputeExecutionLevels(graph);

            for (int level = 0; level < levels.Count; level++)
            {
                float y = level * vSpace;
                for (int i = 0; i < levels[level].Count; i++)
                {
                    var node = levels[level][i];
                    node.Position = new Vector2(i * hSpace + 100, y + 100);
                }
            }
        }

        /// <summary>
        /// 按分类分组排列。
        /// </summary>
        private static void LayoutByCategory(NodeGraph graph, float hSpace, float vSpace)
        {
            var groups = graph.Nodes.GroupBy(n => n.Category).OrderBy(g => g.Key).ToList();
            float x = 100;

            foreach (var group in groups)
            {
                float y = 100;
                foreach (var node in group)
                {
                    node.Position = new Vector2(x, y);
                    y += vSpace;
                }
                x += hSpace + 200;
            }
        }

        /// <summary>
        /// 按数据流方向排列。
        /// </summary>
        private static void LayoutByDataFlow(NodeGraph graph, float hSpace, float vSpace)
        {
            var sorted = graph.TopologicalSort();
            var levels = new List<List<GraphNode>>();
            var nodeLevel = new Dictionary<Guid, int>();

            foreach (var node in sorted)
            {
                int level = 0;
                foreach (var conn in graph.Connections.Where(c => c.TargetNodeId == node.Id))
                {
                    if (nodeLevel.TryGetValue(conn.SourceNodeId, out var srcLevel))
                    {
                        level = Math.Max(level, srcLevel + 1);
                    }
                }

                nodeLevel[node.Id] = level;
                while (levels.Count <= level) levels.Add(new List<GraphNode>());
                levels[level].Add(node);
            }

            for (int l = 0; l < levels.Count; l++)
            {
                float y = l * vSpace + 100;
                for (int i = 0; i < levels[l].Count; i++)
                {
                    levels[l][i].Position = new Vector2(i * hSpace + 100, y);
                }
            }
        }

        /// <summary>
        /// 树形布局。
        /// </summary>
        private static void LayoutAsTree(NodeGraph graph, float hSpace, float vSpace)
        {
            // 找到根节点（没有输入连接的节点）
            var roots = graph.Nodes.Where(n =>
                !graph.Connections.Any(c => c.TargetNodeId == n.Id)).ToList();

            if (roots.Count == 0 && graph.Nodes.Count > 0)
            {
                roots.Add(graph.Nodes[0]);
            }

            var visited = new HashSet<Guid>();
            int xIdx = 0;

            foreach (var root in roots)
            {
                LayoutTreeRecursive(root, 0, ref xIdx, hSpace, vSpace, graph, visited);
            }
        }

        private static void LayoutTreeRecursive(GraphNode node, int depth, ref int xIdx,
            float hSpace, float vSpace, NodeGraph graph, HashSet<Guid> visited)
        {
            if (visited.Contains(node.Id)) return;
            visited.Add(node.Id);

            node.Position = new Vector2(xIdx * hSpace + 100, depth * vSpace + 100);
            xIdx++;

            var children = graph.Connections
                .Where(c => c.SourceNodeId == node.Id)
                .Select(c => graph.GetNode(c.TargetNodeId))
                .Where(n => n != null)
                .ToList();

            foreach (var child in children)
            {
                LayoutTreeRecursive(child!, depth + 1, ref xIdx, hSpace, vSpace, graph, visited);
            }
        }

        /// <summary>
        /// 力导向布局（简化版）。
        /// </summary>
        private static void LayoutForceDirected(NodeGraph graph)
        {
            // 简化的力导向布局
            var nodePositions = graph.Nodes.ToDictionary(n => n.Id, n => n.Position);
            const float repulsion = 5000f;
            const float attraction = 0.01f;
            const float damping = 0.9f;
            const float iterations = 50;

            for (int iter = 0; iter < iterations; iter++)
            {
                var forces = nodePositions.ToDictionary(kvp => kvp.Key, kvp => Vector2.Zero);

                // 斥力
                foreach (var a in graph.Nodes)
                {
                    foreach (var b in graph.Nodes)
                    {
                        if (a.Id == b.Id) continue;
                        var diff = nodePositions[a.Id] - nodePositions[b.Id];
                        var dist = Math.Max(diff.Length(), 1f);
                        var force = diff / dist * repulsion / (dist * dist);
                        forces[a.Id] += force;
                    }
                }

                // 引力（连接的节点之间）
                foreach (var conn in graph.Connections)
                {
                    if (!nodePositions.ContainsKey(conn.SourceNodeId) ||
                        !nodePositions.ContainsKey(conn.TargetNodeId)) continue;
                    var diff = nodePositions[conn.TargetNodeId] - nodePositions[conn.SourceNodeId];
                    var force = diff * attraction;
                    forces[conn.SourceNodeId] += force;
                    forces[conn.TargetNodeId] -= force;
                }

                // 应用力
                foreach (var node in graph.Nodes)
                {
                    nodePositions[node.Id] = (nodePositions[node.Id] + forces[node.Id] * damping);
                    node.Position = nodePositions[node.Id];
                }
            }
        }

        /// <summary>
        /// 计算执行流层级。
        /// </summary>
        private static List<List<GraphNode>> ComputeExecutionLevels(NodeGraph graph)
        {
            var levels = new List<List<GraphNode>>();
            var nodeLevel = new Dictionary<Guid, int>();

            foreach (var node in graph.Nodes)
            {
                int level = 0;
                foreach (var conn in graph.Connections)
                {
                    if (conn.TargetNodeId == node.Id)
                    {
                        if (nodeLevel.TryGetValue(conn.SourceNodeId, out var srcLevel))
                        {
                            level = Math.Max(level, srcLevel + 1);
                        }
                    }
                }

                nodeLevel[node.Id] = level;
                while (levels.Count <= level) levels.Add(new List<GraphNode>());
                levels[level].Add(node);
            }

            return levels;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  VisualScriptEditor - 编辑器主系统
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可视化脚本编辑器主系统：整合画布、菜单、搜索、历史、布局等所有编辑器功能。
    /// </summary>
    [Serializable]
    public class VisualScriptEditor
    {
        /// <summary>编辑器偏好设置。</summary>
        public EditorPreferences Preferences { get; set; } = new EditorPreferences();

        /// <summary>节点画布。</summary>
        public NodeCanvas Canvas { get; set; } = new NodeCanvas();

        /// <summary>连线渲染器。</summary>
        public ConnectionRenderer ConnectionRenderer { get; set; } = new ConnectionRenderer();

        /// <summary>右键菜单。</summary>
        public NodeContextMenu ContextMenu { get; set; } = new NodeContextMenu();

        /// <summary>搜索弹窗。</summary>
        public NodeSearchPopup SearchPopup { get; set; } = new NodeSearchPopup();

        /// <summary>撤销/重做系统。</summary>
        public EditorHistory History { get; set; } = new EditorHistory();

        /// <summary>注释框列表。</summary>
        public List<NodeCommentBox> CommentBoxes { get; set; } = new List<NodeCommentBox>();

        /// <summary>节点分组列表。</summary>
        public List<NodeGroup> Groups { get; set; } = new List<NodeGroup>();

        /// <summary>当前编辑的节点图。</summary>
        public NodeGraph? CurrentGraph { get; set; }

        /// <summary>编辑器是否已修改（需要保存）。</summary>
        public bool IsDirty { get; set; }

        /// <summary>剪贴板（复制的节点 JSON）。</summary>
        private string? _clipboardJson;

        /// <summary>剪贴板中的节点原始位置（用于偏移）。</summary>
        private Vector2 _clipboardOrigin;

        // ───── 节点操作 ─────

        /// <summary>
        /// 在指定画布位置添加节点。
        /// </summary>
        public GraphNode? AddNodeAtPosition(string nodeType, Vector2 canvasPosition)
        {
            if (CurrentGraph == null) return null;

            var node = NodeScriptNodeRegistry.CreateNode(nodeType);
            if (node == null) return null;

            node.Position = canvasPosition;

            History.BeginAction($"Add Node '{node.Title}'", "AddNode", CurrentGraph);
            CurrentGraph.AddNode(node);
            History.EndAction(CurrentGraph);

            IsDirty = true;
            ContextMenu.AddRecentNode(nodeType);
            return node;
        }

        /// <summary>
        /// 删除选中的节点和连接。
        /// </summary>
        public void DeleteSelected()
        {
            if (CurrentGraph == null) return;

            History.BeginAction("Delete Selected", "Delete", CurrentGraph);

            foreach (var nodeId in Canvas.SelectedNodeIds.ToList())
            {
                CurrentGraph.RemoveNode(nodeId);
            }

            foreach (var connId in Canvas.SelectedConnectionIds.ToList())
            {
                CurrentGraph.Disconnect(connId);
            }

            History.EndAction(CurrentGraph);
            Canvas.DeselectAll();
            IsDirty = true;
        }

        /// <summary>
        /// 复制选中的节点。
        /// </summary>
        public void CopySelected()
        {
            if (CurrentGraph == null) return;

            var selectedNodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            if (selectedNodes.Count == 0) return;

            var tempGraph = new NodeGraph();
            foreach (var node in selectedNodes)
            {
                tempGraph.Nodes.Add(node);
            }

            _clipboardJson = tempGraph.SerializeToJson();
            _clipboardOrigin = selectedNodes[0].Position;
        }

        /// <summary>
        /// 粘贴节点。
        /// </summary>
        public void PasteNodes(Vector2 position)
        {
            if (CurrentGraph == null || string.IsNullOrEmpty(_clipboardJson)) return;

            var tempGraph = NodeGraph.DeserializeFromJson(_clipboardJson);
            if (tempGraph == null) return;

            var offset = position - _clipboardOrigin;

            History.BeginAction("Paste Nodes", "Paste", CurrentGraph);

            Canvas.DeselectAll();

            foreach (var node in tempGraph.Nodes)
            {
                node.Id = Guid.NewGuid();
                node.Position += offset;
                CurrentGraph.AddNode(node);
                Canvas.SelectedNodeIds.Add(node.Id);
            }

            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        /// <summary>
        /// 复制选中的节点（原地复制 + 偏移）。
        /// </summary>
        public void DuplicateSelected()
        {
            if (CurrentGraph == null) return;

            var selectedNodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            if (selectedNodes.Count == 0) return;

            History.BeginAction("Duplicate", "Duplicate", CurrentGraph);

            Canvas.DeselectAll();
            var offset = new Vector2(30, 30);

            foreach (var node in selectedNodes)
            {
                var newNode = new GraphNode
                {
                    Id = Guid.NewGuid(),
                    NodeType = node.NodeType,
                    Title = node.Title,
                    Subtitle = node.Subtitle,
                    Category = node.Category,
                    Position = node.Position + offset,
                    Size = node.Size,
                    ColorHex = node.ColorHex,
                    IsCompilable = node.IsCompilable,
                    IsEnabled = node.IsEnabled,
                    Comment = node.Comment,
                    Metadata = new Dictionary<string, string>(node.Metadata)
                };

                foreach (var pin in node.InputPins)
                {
                    var newPin = GraphPin.CreateInput(pin.Name, pin.DataType, pin.DefaultValue, pin.OrderIndex);
                    newPin.IsOptional = pin.IsOptional;
                    newNode.InputPins.Add(newPin);
                }
                foreach (var pin in node.OutputPins)
                {
                    var newPin = GraphPin.CreateOutput(pin.Name, pin.DataType, pin.OrderIndex);
                    newNode.OutputPins.Add(newPin);
                }

                CurrentGraph.AddNode(newNode);
                Canvas.SelectedNodeIds.Add(newNode.Id);
            }

            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        // ───── 撤销/重做 ─────

        /// <summary>撤销。</summary>
        public void Undo()
        {
            if (CurrentGraph == null) return;
            History.Undo(CurrentGraph);
            IsDirty = true;
        }

        /// <summary>重做。</summary>
        public void Redo()
        {
            if (CurrentGraph == null) return;
            History.Redo(CurrentGraph);
            IsDirty = true;
        }

        // ───── 对齐 ─────

        /// <summary>将选中节点左对齐。</summary>
        public void AlignLeft()
        {
            if (CurrentGraph == null || Canvas.SelectedNodeIds.Count < 2) return;
            var nodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            var minX = nodes.Min(n => n.Position.X);
            History.BeginAction("Align Left", "Align", CurrentGraph);
            foreach (var node in nodes) node.Position = new Vector2(minX, node.Position.Y);
            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        /// <summary>将选中节点右对齐。</summary>
        public void AlignRight()
        {
            if (CurrentGraph == null || Canvas.SelectedNodeIds.Count < 2) return;
            var nodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            var maxRight = nodes.Max(n => n.Position.X + n.Size.X);
            History.BeginAction("Align Right", "Align", CurrentGraph);
            foreach (var node in nodes) node.Position = new Vector2(maxRight - node.Size.X, node.Position.Y);
            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        /// <summary>将选中节点顶对齐。</summary>
        public void AlignTop()
        {
            if (CurrentGraph == null || Canvas.SelectedNodeIds.Count < 2) return;
            var nodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            var minY = nodes.Min(n => n.Position.Y);
            History.BeginAction("Align Top", "Align", CurrentGraph);
            foreach (var node in nodes) node.Position = new Vector2(node.Position.X, minY);
            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        /// <summary>将选中节点底对齐。</summary>
        public void AlignBottom()
        {
            if (CurrentGraph == null || Canvas.SelectedNodeIds.Count < 2) return;
            var nodes = CurrentGraph.Nodes.Where(n => Canvas.SelectedNodeIds.Contains(n.Id)).ToList();
            var maxBottom = nodes.Max(n => n.Position.Y + n.Size.Y);
            History.BeginAction("Align Bottom", "Align", CurrentGraph);
            foreach (var node in nodes) node.Position = new Vector2(node.Position.X, maxBottom - node.Size.Y);
            History.EndAction(CurrentGraph);
            IsDirty = true;
        }

        // ───── 注释框 ─────

        /// <summary>在指定位置添加注释框。</summary>
        public NodeCommentBox AddCommentBox(Vector2 position)
        {
            var commentBox = new NodeCommentBox
            {
                Position = position,
                Size = new Vector2(300, 200)
            };

            // 将当前选中的节点加入注释框
            foreach (var nodeId in Canvas.SelectedNodeIds)
            {
                commentBox.AddNode(nodeId);
            }

            if (CurrentGraph != null)
            {
                commentBox.AutoFitToNodes(CurrentGraph);
            }

            CommentBoxes.Add(commentBox);
            IsDirty = true;
            return commentBox;
        }

        // ───── 编译预览 ─────

        /// <summary>
        /// 编译当前图并返回编译结果。
        /// </summary>
        public (bool success, string output, List<GraphValidationMessage> errors)
            CompilePreview(CompileOutputFormat format = CompileOutputFormat.CSharp)
        {
            if (CurrentGraph == null)
                return (false, string.Empty, new List<GraphValidationMessage>());

            var validation = CurrentGraph.Validate();
            var errors = validation.Where(v => v.Severity == ValidationSeverity.Error).ToList();
            var warnings = validation.Where(v => v.Severity == ValidationSeverity.Warning).ToList();

            if (errors.Count > 0)
            {
                return (false, $"编译失败: {errors.Count} 个错误, {warnings.Count} 个警告", errors);
            }

            try
            {
                var output = CurrentGraph.Compile(format);
                return (true, output, validation);
            }
            catch (Exception ex)
            {
                var errorMsg = new GraphValidationMessage
                {
                    Severity = ValidationSeverity.Error,
                    Message = $"编译异常: {ex.Message}"
                };
                errors.Add(errorMsg);
                return (false, $"编译异常: {ex.Message}", errors);
            }
        }

        /// <summary>
        /// 整理布局。
        /// </summary>
        public void TidyLayout(AutoLayoutAlgorithm algorithm = AutoLayoutAlgorithm.ExecutionFlow)
        {
            if (CurrentGraph == null) return;
            History.BeginAction("Auto Layout", "Layout", CurrentGraph);
            NodeAutoLayout.Layout(CurrentGraph, algorithm);
            History.EndAction(CurrentGraph);
            Canvas.FitView(CurrentGraph);
            IsDirty = true;
        }

        // ───── 打开/保存 ─────

        /// <summary>
        /// 打开节点图。
        /// </summary>
        public void OpenGraph(NodeGraph graph)
        {
            CurrentGraph = graph;
            IsDirty = false;
            History.Clear();
            Canvas.DeselectAll();
            Canvas.FitView(graph);
        }

        /// <summary>
        /// 新建节点图。
        /// </summary>
        public NodeGraph NewGraph(string name = "Untitled")
        {
            var graph = new NodeGraph { Name = name, DisplayName = name };
            OpenGraph(graph);
            return graph;
        }

        // ───── 属性面板数据 ─────

        /// <summary>
        /// 获取当前选中节点的属性数据（用于属性面板显示）。
        /// </summary>
        public Dictionary<string, object> GetSelectedNodeProperties()
        {
            var props = new Dictionary<string, object>();

            if (CurrentGraph == null || Canvas.SelectedNodeIds.Count == 0) return props;

            var nodeId = Canvas.SelectedNodeIds.First();
            var node = CurrentGraph.GetNode(nodeId);
            if (node == null) return props;

            props["Id"] = node.Id;
            props["Title"] = node.Title;
            props["NodeType"] = node.NodeType;
            props["Category"] = node.Category;
            props["Position"] = node.Position;
            props["Size"] = node.Size;
            props["ColorHex"] = node.ColorHex;
            props["IsCompilable"] = node.IsCompilable;
            props["IsEnabled"] = node.IsEnabled;
            props["Comment"] = node.Comment ?? string.Empty;
            props["InputPins"] = node.InputPins.Select(p => new { p.Id, p.Name, p.DataType, p.Direction, p.DefaultValue, p.IsOptional, p.IsConnected });
            props["OutputPins"] = node.OutputPins.Select(p => new { p.Id, p.Name, p.DataType, p.Direction, p.IsConnected });

            return props;
        }

        /// <summary>
        /// 获取变量面板数据。
        /// </summary>
        public List<Dictionary<string, object>> GetVariablesPanelData()
        {
            if (CurrentGraph == null) return new List<Dictionary<string, object>>();

            return CurrentGraph.Variables.Select(v => new Dictionary<string, object>
            {
                ["Id"] = v.Id,
                ["Name"] = v.Name,
                ["DataType"] = v.DataType,
                ["AccessLevel"] = v.AccessLevel,
                ["DefaultValue"] = v.DefaultValue ?? string.Empty,
                ["IsSerializable"] = v.IsSerializable
            }).ToList();
        }

        /// <summary>
        /// 获取函数面板数据。
        /// </summary>
        public List<Dictionary<string, object>> GetFunctionsPanelData()
        {
            if (CurrentGraph == null) return new List<Dictionary<string, object>>();

            return CurrentGraph.Functions.Select(f => new Dictionary<string, object>
            {
                ["Id"] = f.Id,
                ["FunctionName"] = f.FunctionName,
                ["AccessModifier"] = f.AccessModifier,
                ["IsPure"] = f.IsPure,
                ["InputCount"] = f.InputPins.Count,
                ["OutputCount"] = f.OutputPins.Count
            }).ToList();
        }
    }
}
