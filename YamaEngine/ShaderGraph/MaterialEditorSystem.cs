// =============================================================================
//  Yama Engine - MaterialEditorSystem.cs
//  yama引擎 - 材质编辑器系统
//
//  本文件实现可视化材质编辑器的运行时数据与逻辑 (不含 UI 控件实现),
//  涵盖:
//    1. MaterialEditor         —— 材质编辑器主窗口 (节点画布/属性面板/预览面板/工具栏)
//    2. MaterialPreview        —— 材质预览 (球体/立方体/圆柱体/平面/角色/自定义模型)
//    3. MaterialPreviewScene   —— 预览渲染场景 (实时渲染/旋转/光照环境/IBL)
//    4. MaterialParameterPanel —— 材质参数面板 (属性列表/编辑/排序/分组/拖拽)
//    5. MaterialInstance       —— 材质实例 (运行时参数修改/不需要重新编译)
//    6. MaterialInheritance    —— 材质继承 (子材质继承父材质属性/覆盖)
//    7. MaterialLOD            —— 材质 LOD (不同距离使用不同着色器/参数过渡)
//    8. MaterialStats          —— 材质统计 (指令数/寄存器数/纹理采样数/变体数/编译时间)
//    9. MaterialCompileError   —— 编译错误显示 (错误节点高亮/错误信息/行号)
//   10. MaterialVariantManager —— 材质变体 (关键词/静态分支/动态分支/变体数量控制)
//
//  设计原则:
//    * 编辑器逻辑与 UI 框架解耦: 本文件仅提供数据模型与操作 API,
//      UI 层 (WPF/WinUI) 通过绑定这些数据实现可视化。
//    * 所有数学使用 System.Numerics
//    * 命名空间: YamaEngine.ShaderGraph
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.Rendering;

namespace YamaEngine.ShaderGraph
{
    // =========================================================================
    // 1. 材质编辑器主窗口
    // =========================================================================

    /// <summary>
    /// 材质编辑器。管理着色器图的编辑状态、选区、预览、属性面板与工具栏。
    /// 这是编辑器的核心数据模型, UI 层通过绑定/订阅实现可视化。
    /// </summary>
    public sealed class MaterialEditor
    {
        /// <summary>当前编辑的着色器图。</summary>
        public ShaderGraph Graph { get; private set; }

        /// <summary>预览系统。</summary>
        public MaterialPreview Preview { get; }

        /// <summary>参数面板。</summary>
        public MaterialParameterPanel ParameterPanel { get; }

        /// <summary>变体管理器。</summary>
        public MaterialVariantManager VariantManager { get; }

        /// <summary>选中的节点 ID 列表。</summary>
        public HashSet<string> SelectedNodeIds { get; } = new HashSet<string>();

        /// <summary>选中的连接 ID。</summary>
        public string? SelectedConnectionId { get; set; }

        /// <summary>画布缩放。</summary>
        public float Zoom { get; set; } = 1.0f;

        /// <summary>画布平移。</summary>
        public Vector2 Pan { get; set; } = Vector2.Zero;

        /// <summary>是否显示网格。</summary>
        public bool ShowGrid { get; set; } = true;

        /// <summary>是否启用节点预览。</summary>
        public bool EnableNodePreview { get; set; } = true;

        /// <summary>是否自动编译。</summary>
        public bool AutoCompile { get; set; } = true;

        /// <summary>编译统计。</summary>
        public MaterialStats? LastStats { get; set; }

        /// <summary>编译错误列表。</summary>
        public List<MaterialCompileError> CompileErrors { get; } = new List<MaterialCompileError>();

        /// <summary>撤销/重做栈。</summary>
        private readonly Stack<EditorAction> _undoStack = new Stack<EditorAction>();
        private readonly Stack<EditorAction> _redoStack = new Stack<EditorAction>();

        /// <summary>编辑器是否已修改 (未保存)。</summary>
        public bool IsDirty { get; set; }

        /// <summary>构造材质编辑器。</summary>
        public MaterialEditor(ShaderGraph? graph = null)
        {
            Graph = graph ?? CreateDefaultGraph();
            Preview = new MaterialPreview(Graph);
            ParameterPanel = new MaterialParameterPanel(Graph);
            VariantManager = new MaterialVariantManager(Graph);
        }

        /// <summary>创建默认着色器图 (含 PBR 主节点)。</summary>
        public static ShaderGraph CreateDefaultGraph()
        {
            var graph = new ShaderGraph
            {
                Name = "New Material",
                Target = ShaderGraphTarget.Universal,
                Precision = ShaderGraphPrecision.Float,
                BlendMode = ShaderGraphBlendMode.Opaque,
                ShadingModel = ShaderGraphShadingModel.Lit
            };
            var master = new PBRMasterNode { Position = new Vector2(600, 200) };
            graph.SetOutputNode(master);
            return graph;
        }

        // ── 节点操作 ──────────────────────────────────────────

        /// <summary>在画布指定位置添加节点。</summary>
        public ShaderGraphNode AddNode(string nodeType, Vector2 position)
        {
            var node = ShaderNodeRegistry.CreateNode(nodeType);
            if (node == null) return null!;
            node.Position = position;
            Graph.AddNode(node);
            IsDirty = true;
            PushUndo(new AddNodeAction(node));
            return node;
        }

        /// <summary>删除选中的节点。</summary>
        public void DeleteSelectedNodes()
        {
            var toRemove = SelectedNodeIds.Select(id => Graph.GetNode(id)).Where(n => n != null).ToList();
            foreach (var node in toRemove)
            {
                if (node!.IsMasterNode) continue; // 不允许删除主节点
                Graph.RemoveNode(node.Id);
            }
            SelectedNodeIds.Clear();
            IsDirty = true;
        }

        /// <summary>选中节点。</summary>
        public void SelectNode(string nodeId, bool additive = false)
        {
            if (!additive) SelectedNodeIds.Clear();
            SelectedNodeIds.Add(nodeId);
        }

        /// <summary>全选节点。</summary>
        public void SelectAll()
        {
            SelectedNodeIds.Clear();
            foreach (var n in Graph.Nodes)
                SelectedNodeIds.Add(n.Id);
        }

        /// <summary>清除选区。</summary>
        public void ClearSelection()
        {
            SelectedNodeIds.Clear();
            SelectedConnectionId = null;
        }

        /// <summary>创建连接。</summary>
        public ShaderGraphConnection? Connect(
            string srcNodeId, string srcPinName,
            string dstNodeId, string dstPinName)
        {
            var conn = Graph.Connect(srcNodeId, srcPinName, dstNodeId, dstPinName);
            if (conn != null)
            {
                IsDirty = true;
                if (AutoCompile) RequestRecompile();
            }
            return conn;
        }

        /// <summary>断开连接。</summary>
        public void Disconnect(string connectionId)
        {
            Graph.Disconnect(connectionId);
            IsDirty = true;
            if (AutoCompile) RequestRecompile();
        }

        /// <summary>移动选中节点。</summary>
        public void MoveSelectedNodes(Vector2 delta)
        {
            foreach (var id in SelectedNodeIds)
            {
                var node = Graph.GetNode(id);
                if (node != null) node.Position += delta;
            }
            IsDirty = true;
        }

        // ── 属性操作 ──────────────────────────────────────────

        /// <summary>添加属性。</summary>
        public ShaderProperty AddProperty(string name, ShaderPropertyType type)
        {
            var prop = new ShaderProperty
            {
                Name = name,
                DisplayName = name,
                PropertyType = type,
                Group = "Default"
            };
            Graph.AddProperty(prop);
            ParameterPanel.Refresh();
            IsDirty = true;
            return prop;
        }

        /// <summary>删除属性。</summary>
        public void RemoveProperty(string propertyId)
        {
            var prop = Graph.Properties.FirstOrDefault(p => p.Id == propertyId);
            if (prop == null) return;
            Graph.Properties.Remove(prop);
            ParameterPanel.Refresh();
            IsDirty = true;
        }

        // ── 预览与编译 ────────────────────────────────────────

        /// <summary>请求重新编译 (触发编译流程)。</summary>
        public void RequestRecompile()
        {
            // 实际编译由 ShaderGraphCompiler 执行, 编辑器仅标记需要重编
            Preview.MarkDirty();
            if (EnableNodePreview) Graph.UpdatePreviews();
        }

        /// <summary>更新预览。</summary>
        public void UpdatePreview(float deltaTime)
        {
            Preview.Update(deltaTime);
        }

        // ── 撤销/重做 ────────────────────────────────────────

        /// <summary>撤销。</summary>
        public bool Undo()
        {
            if (_undoStack.Count == 0) return false;
            var action = _undoStack.Pop();
            action.Undo(Graph);
            _redoStack.Push(action);
            IsDirty = true;
            return true;
        }

        /// <summary>重做。</summary>
        public bool Redo()
        {
            if (_redoStack.Count == 0) return false;
            var action = _redoStack.Pop();
            action.Redo(Graph);
            _undoStack.Push(action);
            IsDirty = true;
            return true;
        }

        private void PushUndo(EditorAction action)
        {
            _undoStack.Push(action);
            _redoStack.Clear();
        }

        /// <summary>加载着色器图。</summary>
        public void LoadGraph(ShaderGraph graph)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            Preview.SetGraph(graph);
            ParameterPanel.SetGraph(graph);
            VariantManager.SetGraph(graph);
            SelectedNodeIds.Clear();
            CompileErrors.Clear();
            IsDirty = false;
        }

        /// <summary>获取画布中指定矩形区域内的节点。</summary>
        public List<ShaderGraphNode> GetNodesInRect(Vector2 min, Vector2 max)
        {
            return Graph.Nodes.Where(n =>
                n.Position.X >= min.X && n.Position.X <= max.X &&
                n.Position.Y >= min.Y && n.Position.Y <= max.Y).ToList();
        }

        /// <summary>对齐选中节点。</summary>
        public void AlignSelectedNodes(NodeAlignment mode)
        {
            var nodes = SelectedNodeIds
                .Select(id => Graph.GetNode(id))
                .Where(n => n != null).ToList();
            if (nodes.Count < 2) return;

            switch (mode)
            {
                case NodeAlignment.Left:
                    var minX = nodes.Min(n => n!.Position.X);
                    foreach (var n in nodes) n!.Position = new Vector2(minX, n.Position.Y);
                    break;
                case NodeAlignment.Right:
                    var maxX = nodes.Max(n => n!.Position.X);
                    foreach (var n in nodes) n!.Position = new Vector2(maxX, n.Position.Y);
                    break;
                case NodeAlignment.Top:
                    var minY = nodes.Min(n => n!.Position.Y);
                    foreach (var n in nodes) n!.Position = new Vector2(n.Position.X, minY);
                    break;
                case NodeAlignment.Bottom:
                    var maxY = nodes.Max(n => n!.Position.Y);
                    foreach (var n in nodes) n!.Position = new Vector2(n.Position.X, maxY);
                    break;
                case NodeAlignment.HorizontalCenter:
                    var avgY = nodes.Average(n => n!.Position.Y);
                    foreach (var n in nodes) n!.Position = new Vector2(n.Position.X, (float)avgY);
                    break;
                case NodeAlignment.VerticalCenter:
                    var avgX = nodes.Average(n => n!.Position.X);
                    foreach (var n in nodes) n!.Position = new Vector2((float)avgX, n.Position.Y);
                    break;
            }
            IsDirty = true;
        }
    }

    /// <summary>节点对齐模式。</summary>
    public enum NodeAlignment
    {
        Left, Right, Top, Bottom,
        HorizontalCenter, VerticalCenter
    }

    // =========================================================================
    // 编辑器动作 (撤销/重做)
    // =========================================================================

    /// <summary>编辑器动作基类。</summary>
    public abstract class EditorAction
    {
        public abstract void Undo(ShaderGraph graph);
        public abstract void Redo(ShaderGraph graph);
    }

    /// <summary>添加节点动作。</summary>
    public sealed class AddNodeAction : EditorAction
    {
        private readonly ShaderGraphNode _node;
        public AddNodeAction(ShaderGraphNode node) { _node = node; }
        public override void Undo(ShaderGraph graph) => graph.RemoveNode(_node.Id);
        public override void Redo(ShaderGraph graph) => graph.AddNode(_node);
    }

    // =========================================================================
    // 2. 材质预览
    // =========================================================================

    /// <summary>预览模型类型。</summary>
    public enum PreviewModelType
    {
        Sphere,
        Cube,
        Cylinder,
        Plane,
        Torus,
        Capsule,
        Character,
        Custom
    }

    /// <summary>预览光照环境。</summary>
    public enum PreviewLightingEnvironment
    {
        Studio,
        Outdoor,
        Indoor,
        Sunset,
        Night,
        CustomIBL
    }

    /// <summary>
    /// 材质预览。实时渲染材质应用到预览模型, 支持旋转/光照环境/IBL。
    /// </summary>
    public sealed class MaterialPreview
    {
        private ShaderGraph _graph;

        /// <summary>预览模型类型。</summary>
        public PreviewModelType ModelType { get; set; } = PreviewModelType.Sphere;

        /// <summary>光照环境。</summary>
        public PreviewLightingEnvironment Lighting { get; set; } = PreviewLightingEnvironment.Studio;

        /// <summary>模型旋转角度 (弧度)。</summary>
        public float RotationY { get; set; }

        /// <summary>是否自动旋转。</summary>
        public bool AutoRotate { get; set; } = true;

        /// <summary>自动旋转速度 (弧度/秒)。</summary>
        public float AutoRotateSpeed { get; set; } = 0.5f;

        /// <summary>摄像机距离。</summary>
        public float CameraDistance { get; set; } = 3.0f;

        /// <summary>摄像机俯仰角。</summary>
        public float CameraPitch { get; set; } = 0.2f;

        /// <summary>预览尺寸。</summary>
        public Vector2 PreviewSize { get; set; } = new Vector2(256, 256);

        /// <summary>IBL 环境贴图路径 (CustomIBL 模式)。</summary>
        public string? IBLTexturePath { get; set; }

        /// <summary>是否显示网格地面。</summary>
        public bool ShowGround { get; set; } = true;

        /// <summary>是否显示坐标轴。</summary>
        public bool ShowAxes { get; set; } = false;

        /// <summary>预览是否过期。</summary>
        public bool IsDirty { get; private set; } = true;

        /// <summary>渲染的预览图像数据 (RGBA, 由渲染线程填充)。</summary>
        public byte[]? PreviewPixels { get; set; }

        public MaterialPreview(ShaderGraph graph)
        {
            _graph = graph;
        }

        public void SetGraph(ShaderGraph graph)
        {
            _graph = graph;
            IsDirty = true;
        }

        /// <summary>标记预览过期。</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>更新预览 (每帧调用)。</summary>
        public void Update(float deltaTime)
        {
            if (AutoRotate)
                RotationY += AutoRotateSpeed * deltaTime;

            if (IsDirty)
            {
                // 实际渲染由渲染系统在 GPU 上执行
                // 此处仅标记状态
                IsDirty = false;
            }
        }

        /// <summary>重置摄像机。</summary>
        public void ResetCamera()
        {
            RotationY = 0;
            CameraDistance = 3.0f;
            CameraPitch = 0.2f;
        }

        /// <summary>设置预览模型。</summary>
        public void SetModel(PreviewModelType type)
        {
            ModelType = type;
            IsDirty = true;
        }

        /// <summary>设置光照环境。</summary>
        public void SetLighting(PreviewLightingEnvironment env)
        {
            Lighting = env;
            IsDirty = true;
        }

        /// <summary>获取预览摄像机位置。</summary>
        public Vector3 GetCameraPosition()
        {
            var x = (float)Math.Sin(RotationY) * CameraDistance * (float)Math.Cos(CameraPitch);
            var y = (float)Math.Sin(CameraPitch) * CameraDistance;
            var z = (float)Math.Cos(RotationY) * CameraDistance * (float)Math.Cos(CameraPitch);
            return new Vector3(x, y, z);
        }
    }

    /// <summary>
    /// 材质预览场景。管理预览渲染所需的光照/环境/网格资源。
    /// </summary>
    public sealed class MaterialPreviewScene
    {
        /// <summary>方向光方向。</summary>
        public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f));

        /// <summary>方向光强度。</summary>
        public float SunIntensity { get; set; } = 3.0f;

        /// <summary>方向光颜色。</summary>
        public Vector3 SunColor { get; set; } = new Vector3(1.0f, 0.95f, 0.85f);

        /// <summary>环境光颜色。</summary>
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.25f, 0.3f);

        /// <summary>天空盒贴图路径。</summary>
        public string? SkyboxPath { get; set; }

        /// <summary>IBL 辐照度贴图路径。</summary>
        public string? IrradianceMapPath { get; set; }

        /// <summary>IBL 预滤波贴图路径。</summary>
        public string? PrefilterMapPath { get; set; }

        /// <summary>BRDF LUT 贴图路径。</summary>
        public string? BRDFLutPath { get; set; }

        /// <summary>背景颜色 (无天空盒时)。</summary>
        public Vector4 BackgroundColor { get; set; } = new Vector4(0.15f, 0.15f, 0.18f, 1.0f);

        /// <summary>应用预设光照环境。</summary>
        public void ApplyPreset(PreviewLightingEnvironment env)
        {
            switch (env)
            {
                case PreviewLightingEnvironment.Studio:
                    SunDirection = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f));
                    SunIntensity = 3.0f;
                    SunColor = new Vector3(1.0f, 0.95f, 0.85f);
                    AmbientColor = new Vector3(0.2f, 0.25f, 0.3f);
                    break;
                case PreviewLightingEnvironment.Outdoor:
                    SunDirection = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.5f));
                    SunIntensity = 4.5f;
                    SunColor = new Vector3(1.0f, 0.98f, 0.92f);
                    AmbientColor = new Vector3(0.4f, 0.5f, 0.6f);
                    break;
                case PreviewLightingEnvironment.Sunset:
                    SunDirection = Vector3.Normalize(new Vector3(0.8f, 0.2f, 0.4f));
                    SunIntensity = 2.5f;
                    SunColor = new Vector3(1.0f, 0.5f, 0.2f);
                    AmbientColor = new Vector3(0.3f, 0.15f, 0.1f);
                    break;
                case PreviewLightingEnvironment.Night:
                    SunDirection = Vector3.Normalize(new Vector3(-0.3f, 0.6f, -0.5f));
                    SunIntensity = 0.5f;
                    SunColor = new Vector3(0.6f, 0.7f, 1.0f);
                    AmbientColor = new Vector3(0.05f, 0.05f, 0.1f);
                    break;
            }
        }
    }

    // =========================================================================
    // 3. 材质参数面板
    // =========================================================================

    /// <summary>
    /// 材质参数面板。管理图属性的编辑/排序/分组/拖拽状态。
    /// </summary>
    public sealed class MaterialParameterPanel
    {
        private ShaderGraph _graph;

        /// <summary>分组列表 (按分组组织的属性)。</summary>
        public List<ParameterGroup> Groups { get; private set; } = new List<ParameterGroup>();

        public MaterialParameterPanel(ShaderGraph graph)
        {
            _graph = graph;
            Refresh();
        }

        public void SetGraph(ShaderGraph graph)
        {
            _graph = graph;
            Refresh();
        }

        /// <summary>刷新分组列表。</summary>
        public void Refresh()
        {
            Groups.Clear();
            var grouped = _graph.Properties
                .GroupBy(p => p.Group)
                .OrderBy(g => g.Key);

            foreach (var g in grouped)
            {
                var group = new ParameterGroup
                {
                    Name = g.Key,
                    Expanded = true
                };
                group.Properties.AddRange(g.OrderBy(p => p.SortOrder));
                Groups.Add(group);
            }
        }

        /// <summary>设置属性值 (标量)。</summary>
        public void SetScalar(string propertyName, float value)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.ScalarValue = value;
                Refresh();
            }
        }

        /// <summary>设置属性值 (向量/颜色)。</summary>
        public void SetVector(string propertyName, Vector4 value)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.VectorValue = value;
                Refresh();
            }
        }

        /// <summary>设置属性值 (纹理路径)。</summary>
        public void SetTexture(string propertyName, string path)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.TexturePath = path;
                Refresh();
            }
        }

        /// <summary>设置属性值 (布尔)。</summary>
        public void SetBool(string propertyName, bool value)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.BoolValue = value;
                Refresh();
            }
        }

        /// <summary>移动属性到指定分组。</summary>
        public void MoveToGroup(string propertyName, string groupName)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.Group = groupName;
                Refresh();
            }
        }

        /// <summary>重排序属性。</summary>
        public void Reorder(string propertyName, int newSortOrder)
        {
            var prop = _graph.GetProperty(propertyName);
            if (prop != null)
            {
                prop.SortOrder = newSortOrder;
                Refresh();
            }
        }

        /// <summary>折叠/展开分组。</summary>
        public void ToggleGroup(string groupName)
        {
            var g = Groups.FirstOrDefault(x => x.Name == groupName);
            if (g != null) g.Expanded = !g.Expanded;
        }
    }

    /// <summary>参数分组。</summary>
    public sealed class ParameterGroup
    {
        public string Name { get; set; } = "Default";
        public bool Expanded { get; set; } = true;
        public List<ShaderProperty> Properties { get; set; } = new List<ShaderProperty>();
    }

    // =========================================================================
    // 4. 材质实例
    // =========================================================================

    /// <summary>
    /// 材质实例。基于已编译的材质, 运行时修改参数而无需重新编译。
    /// 通过覆盖父材质的属性值实现。
    /// </summary>

    /// <summary>材质参数值 (覆盖值容器)。</summary>
    public sealed class MaterialParameterValue
    {
        public ShaderPropertyType Type { get; set; }
        public float ScalarValue { get; set; }
        public Vector4 VectorValue { get; set; }
        public string TexturePath { get; set; } = string.Empty;
        public bool BoolValue { get; set; }
        public int IntValue { get; set; }
    }

    // =========================================================================
    // 5. 材质继承
    // =========================================================================

    /// <summary>
    /// 材质继承系统。子材质继承父材质的图结构与属性, 可覆盖部分属性。
    /// </summary>
    public sealed class MaterialInheritance
    {
        /// <summary>父材质 GUID。</summary>
        public string ParentGuid { get; set; } = string.Empty;

        /// <summary>子材质 GUID。</summary>
        public string ChildGuid { get; set; } = string.Empty;

        /// <summary>被覆盖的属性名列表。</summary>
        public HashSet<string> OverriddenProperties { get; set; } = new HashSet<string>();

        /// <summary>从父材质继承并应用覆盖。</summary>
        public ShaderGraph Inherit(ShaderGraph parent, MaterialInstance overrides)
        {
            var child = parent.Clone();
            child.Guid = Guid.NewGuid().ToString("N");
            child.Name = parent.Name + "_Child";

            foreach (var prop in child.Properties)
            {
                overrides.ApplyTo(prop);
                if (overrides.HasOverride(prop.Name))
                    OverriddenProperties.Add(prop.Name);
            }
            return child;
        }
    }

    // =========================================================================
    // 6. 材质 LOD
    // =========================================================================

    /// <summary>
    /// 材质 LOD。不同距离使用不同着色器/参数, 并在 LOD 之间过渡。
    /// </summary>
    public sealed class MaterialLOD
    {
        /// <summary>LOD 级别列表。</summary>
        public List<MaterialLODLevel> Levels { get; set; } = new List<MaterialLODLevel>();

        /// <summary>添加 LOD 级别。</summary>
        public void AddLevel(float screenSize, ShaderGraph graph)
        {
            Levels.Add(new MaterialLODLevel
            {
                ScreenSize = screenSize,
                Graph = graph,
                LODIndex = Levels.Count
            });
            Levels.Sort((a, b) => b.ScreenSize.CompareTo(a.ScreenSize));
        }

        /// <summary>根据屏幕占比选择 LOD 级别。</summary>
        public MaterialLODLevel? SelectLOD(float screenSize)
        {
            foreach (var level in Levels)
            {
                if (screenSize >= level.ScreenSize)
                    return level;
            }
            return Levels.LastOrDefault();
        }

        /// <summary>LOD 过渡因子 (0=当前级别, 1=下一级别)。</summary>
        public float GetTransitionFactor(float screenSize)
        {
            var current = SelectLOD(screenSize);
            if (current == null) return 0;
            var idx = Levels.IndexOf(current);
            if (idx + 1 >= Levels.Count) return 0;
            var next = Levels[idx + 1];
            return 1.0f - (screenSize - next.ScreenSize) / (current.ScreenSize - next.ScreenSize);
        }
    }

    /// <summary>材质 LOD 级别。</summary>
    public sealed class MaterialLODLevel
    {
        public int LODIndex { get; set; }
        public float ScreenSize { get; set; } = 1.0f;
        public ShaderGraph? Graph { get; set; }
        public bool EnableDithering { get; set; } = true;
    }

    // =========================================================================
    // 7. 材质统计
    // =========================================================================

    /// <summary>
    /// 材质编译统计。包含指令数/寄存器数/纹理采样数/变体数/编译时间。
    /// </summary>
    public sealed class MaterialStats
    {
        /// <summary>顶点着色器指令数。</summary>
        public int VSInstructionCount { get; set; }

        /// <summary>像素着色器指令数。</summary>
        public int PSInstructionCount { get; set; }

        /// <summary>顶点着色器寄存器数。</summary>
        public int VSRegisterCount { get; set; }

        /// <summary>像素着色器寄存器数。</summary>
        public int PSRegisterCount { get; set; }

        /// <summary>纹理采样次数。</summary>
        public int TextureSampleCount { get; set; }

        /// <summary>变体数量。</summary>
        public int VariantCount { get; set; }

        /// <summary>编译耗时 (毫秒)。</summary>
        public long CompileTimeMs { get; set; }

        /// <summary>生成代码行数。</summary>
        public int CodeLineCount { get; set; }

        /// <summary>节点总数。</summary>
        public int NodeCount { get; set; }

        /// <summary>属性总数。</summary>
        public int PropertyCount { get; set; }

        /// <summary>常量缓冲区大小 (字节)。</summary>
        public int ConstantBufferSize { get; set; }

        /// <summary>生成统计摘要字符串。</summary>
        public string GetSummary()
        {
            return $"VS: {VSInstructionCount} inst / {VSRegisterCount} regs, " +
                   $"PS: {PSInstructionCount} inst / {PSRegisterCount} regs, " +
                   $"TexSamples: {TextureSampleCount}, Variants: {VariantCount}, " +
                   $"Compile: {CompileTimeMs}ms, Code: {CodeLineCount} lines, " +
                   $"CB Size: {ConstantBufferSize}B";
        }
    }

    // =========================================================================
    // 8. 编译错误显示
    // =========================================================================

    /// <summary>材质编译错误。</summary>
    public sealed class MaterialCompileError
    {
        /// <summary>错误级别。</summary>
        public ValidationErrorLevel Level { get; set; }

        /// <summary>错误消息。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>关联的节点 ID (错误节点高亮)。</summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>关联的引脚 ID。</summary>
        public string PinId { get; set; } = string.Empty;

        /// <summary>源码行号。</summary>
        public int Line { get; set; }

        /// <summary>源码列号。</summary>
        public int Column { get; set; }

        /// <summary>错误代码。</summary>
        public string ErrorCode { get; set; } = string.Empty;

        public override string ToString()
        {
            var loc = Line > 0 ? $" (Line {Line}" + (Column > 0 ? $":{Column}" : "") + ")" : "";
            var node = !string.IsNullOrEmpty(NodeId) ? $" [Node: {NodeId.Substring(0, Math.Min(8, NodeId.Length))}]" : "";
            return $"[{Level}] {Message}{loc}{node}";
        }
    }

    // =========================================================================
    // 9. 材质变体管理器
    // =========================================================================

    /// <summary>
    /// 材质变体管理器。管理着色器关键字/静态分支/动态分支, 控制变体数量。
    /// </summary>
    public sealed class MaterialVariantManager
    {
        private ShaderGraph _graph;

        /// <summary>静态关键字 (影响变体)。</summary>
        public List<MaterialKeyword> StaticKeywords { get; set; } = new List<MaterialKeyword>();

        /// <summary>动态关键字 (运行时切换, 不生成新变体)。</summary>
        public List<MaterialKeyword> DynamicKeywords { get; set; } = new List<MaterialKeyword>();

        /// <summary>最大变体数量限制。</summary>
        public int MaxVariantCount { get; set; } = 1024;

        public MaterialVariantManager(ShaderGraph graph)
        {
            _graph = graph;
        }

        public void SetGraph(ShaderGraph graph)
        {
            _graph = graph;
        }

        /// <summary>添加静态关键字。</summary>
        public void AddStaticKeyword(string name, bool defaultValue = false, string description = "")
        {
            StaticKeywords.Add(new MaterialKeyword
            {
                Name = name,
                DefaultValue = defaultValue,
                IsStatic = true,
                Description = description
            });
            _graph.Keywords.Add(name);
        }

        /// <summary>添加动态关键字。</summary>
        public void AddDynamicKeyword(string name, bool defaultValue = false, string description = "")
        {
            DynamicKeywords.Add(new MaterialKeyword
            {
                Name = name,
                DefaultValue = defaultValue,
                IsStatic = false,
                Description = description
            });
        }

        /// <summary>计算变体总数 (静态关键字的笛卡尔积)。</summary>
        public int ComputeVariantCount()
        {
            if (StaticKeywords.Count == 0) return 1;
            long count = 1;
            foreach (var kw in StaticKeywords)
                count *= 2; // 每个关键字 2 种状态
            return (int)Math.Min(count, MaxVariantCount);
        }

        /// <summary>生成所有变体的关键字组合。</summary>
        public List<HashSet<string>> GenerateVariants()
        {
            var result = new List<HashSet<string>>();
            var staticKws = StaticKeywords;
            int total = 1 << staticKws.Count;
            if (total > MaxVariantCount) total = MaxVariantCount;

            for (int i = 0; i < total; i++)
            {
                var set = new HashSet<string>();
                for (int j = 0; j < staticKws.Count; j++)
                {
                    if ((i & (1 << j)) != 0)
                        set.Add(staticKws[j].Name);
                }
                result.Add(set);
            }
            return result;
        }

        /// <summary>根据静态开关值选择变体。</summary>
        public HashSet<string> SelectVariant(Dictionary<string, bool> staticSwitchValues)
        {
            var set = new HashSet<string>();
            foreach (var kw in StaticKeywords)
            {
                if (staticSwitchValues.TryGetValue(kw.Name, out var enabled) && enabled)
                    set.Add(kw.Name);
                else if (kw.DefaultValue)
                    set.Add(kw.Name);
            }
            return set;
        }

        /// <summary>移除关键字。</summary>
        public void RemoveKeyword(string name)
        {
            StaticKeywords.RemoveAll(k => k.Name == name);
            DynamicKeywords.RemoveAll(k => k.Name == name);
            _graph.Keywords.Remove(name);
        }
    }

    /// <summary>材质关键字。</summary>
    public sealed class MaterialKeyword
    {
        /// <summary>关键字名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>默认值。</summary>
        public bool DefaultValue { get; set; }

        /// <summary>是否为静态关键字 (影响变体)。</summary>
        public bool IsStatic { get; set; } = true;

        /// <summary>描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>所属分组。</summary>
        public string Group { get; set; } = "Default";
    }
}
