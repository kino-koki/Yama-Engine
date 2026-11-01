using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using YamaEngine.Assets;
using YamaEngine.Config;
using YamaEngine.ECS;
using YamaEngine.Editor.Panels;
using YamaEngine.Input;
using YamaEngine.Rendering;
using YamaEngine.RHI;

namespace YamaEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 编辑器主窗口 (MainEditorWindow)
    //
    // 编辑器的核心编排器, 负责:
    //   * ImGui DockSpace 布局系统 (面板拖拽停靠 / 浮动 / 标签页)
    //   * 顶部菜单栏 (File / Edit / Window / Help)
    //   * 顶部工具栏 (PIE 控制 / 变换模式 / 捕捉 / 网格 / 渲染模式)
    //   * 底部状态栏 (FPS / 实体数 / DrawCall / GPU内存 / 选中对象)
    //   * 面板注册系统 (可插拔 IEditorPanel)
    //   * 持久化布局保存 / 加载 (JSON)
    //   * 快捷键系统
    //   * Gizmo 系统
    //   * 视口渲染集成 (ImGui.Image 显示离屏渲染纹理)
    //   * 面板间通信 (选择同步 / 资产拖放)
    //   * 控制台命令注册
    //
    // 生命周期:
    //   1. 构造: 创建视口 / 面板 / 上下文 / 系统
    //   2. Initialize(): 注册命令 / 快捷键 / 创建 SRV / 应用主题
    //   3. Render(dt): 每帧调用, 更新 + 绘制
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器主窗口: 管理所有面板的布局、生命周期与通信。</summary>
    public sealed class MainEditorWindow
    {
        // ── 核心系统引用 ──
        private readonly EditorWorld _world;
        private readonly InputSystem _input;
        private readonly RHIDevice _device;
        private readonly AssetDatabase _assets;
        private readonly EntityComponentSystem _ecs;
        private readonly EditorContext _ctx;

        // ── 视口 ──
        public EditorViewport Viewport { get; }
        private IntPtr _viewportTextureId;
        private RHIDescriptorHandle _viewportSrv;
        private int _lastViewportW, _lastViewportH;
        private bool _viewportSrvValid;

        // ── 面板 (公开属性供面板间通信) ──
        public OutlinerPanel Outliner { get; }
        public InspectorPanel Inspector { get; }
        public ContentBrowserPanel ContentBrowser { get; }
        public ToolbarPanel Toolbar { get; }
        public ConsolePanel Console { get; }
        public StatsPanel Stats { get; }
        public CodeEditorPanel CodeEditor { get; }
        public TerrainEditorPanel TerrainEditor { get; }
        public ModelingPanel ModelingTool { get; }

        // ── 编辑器模式系统 ──
        public UnifiedEditorModeSystem ModeSystem { get; }

        private readonly List<IEditorPanel> _allPanels = new();

        // ── 编辑器系统 ──
        public CommandRegistry Commands { get; }
        public EditorShortcutManager Shortcuts { get; }
        public EditorGizmo GizmoManager { get; }
        public DockSpace DockSpace { get; }

        // ── 布局 ──
        private bool _firstLayout = true;
        private string _layoutPath = "editor_layout.json";
        private bool _dockingInitialized;

        // ── 弹窗状态 ──
        private bool _showOpenScene;
        private bool _showSaveAs;
        private bool _showAbout;
        private bool _showShortcuts;
        private bool _showImport;
        private string _scenePathInput = "scene.json";
        private string _importPathInput = "";

        // ── 统计 ──
        private float _statsUpdateTimer;
        private float _frameTimeAccum;
        private int _frameCount;
        private float _smoothedFps = 60;

        // ── 帧时间分解 (模拟值, 可由引擎填充) ──
        private FrameTimeBreakdown _frameBreakdown;
        private MemoryStats _memoryStats;
        private DrawCallStats _drawCallStats;

        // ══════════════════════════ 构造 ══════════════════════════

        public MainEditorWindow(EditorWorld world, InputSystem input)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _device = world.Device;
            _assets = world.Assets;
            _ecs = world.Ecs;

            // 创建视口
            Viewport = new EditorViewport(_device, world.RenderDevice, _assets, _ecs, 1280, 720);

            // 创建面板
            Outliner = new OutlinerPanel();
            Inspector = new InspectorPanel();
            ContentBrowser = new ContentBrowserPanel();
            Toolbar = new ToolbarPanel();
            Console = new ConsolePanel();
            Stats = new StatsPanel();
            CodeEditor = new CodeEditorPanel();
            TerrainEditor = new TerrainEditorPanel();
            ModelingTool = new ModelingPanel();

            // 创建编辑器模式系统
            ModeSystem = new UnifiedEditorModeSystem();

            _allPanels.Add(Outliner);
            _allPanels.Add(Inspector);
            _allPanels.Add(ContentBrowser);
            _allPanels.Add(Toolbar);
            _allPanels.Add(Console);
            _allPanels.Add(Stats);
            _allPanels.Add(CodeEditor);
            _allPanels.Add(TerrainEditor);
            _allPanels.Add(ModelingTool);

            // 创建停靠空间
            DockSpace = new DockSpace(new Vector2(1280, 720));
            foreach (var p in _allPanels)
                DockSpace.RegisterPanel(p);

            // 创建编辑器系统
            Commands = new CommandRegistry();
            Shortcuts = new EditorShortcutManager();
            GizmoManager = new EditorGizmo(Viewport.Gizmo, _assets, _world.UndoStack);

            // 创建上下文
            var theme = new EditorTheme();
            theme.ApplyPreset(EditorThemePreset.Dark);
            _ctx = new EditorContext(_world, _assets, _ecs, _input, theme)
            {
                Console = Console,
                Editor = this
            };
        }

        /// <summary>初始化: 注册命令 / 快捷键 / 创建 SRV / 应用主题。</summary>
        public void Initialize()
        {
            // 启用 ImGui 停靠
            try
            {
                var io = ImGui.GetIO();
                io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
                io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
                io.ConfigWindowsMoveFromTitleBarOnly = true;
            }
            catch (Exception) { /* ImGui 未初始化 */ }

            // 应用主题
            _ctx.Theme?.ApplyToImGuiStyle();

            // 初始化面板
            foreach (var p in _allPanels)
                p.Initialize(_ctx);

            // 创建视口 SRV
            CreateViewportSrv();

            // 注册命令
            RegisterCommands();

            // 注册快捷键
            Shortcuts.Initialize(_ctx);

            // 日志
            Console.AddLog(LogLevel.Info, "yama引擎编辑器已初始化", "editor");
            Console.AddLog(LogLevel.Debug, $"ImGui 版本: {GetImGuiVersion()}", "imgui");
            Console.AddLog(LogLevel.Info, "代码编辑器已就绪 (Ctrl+Space 补全, Ctrl+F 搜索)", "code");
            Console.AddLog(LogLevel.Info, "一体化工作流: 按 1-5 切换场景/地形/建模/代码/测试模式", "mode");
            Console.AddLog(LogLevel.Info, "命令: terrain(地形) / model(建模) / code(代码) / mode(切换模式)", "cmd");
        }

        // ══════════════════════════ 主渲染 ══════════════════════════

        /// <summary>每帧渲染编辑器 UI (由宿主程序调用)。</summary>
        public void Render(float dt)
        {
            // ── 更新上下文 ──
            _ctx.DeltaTime = dt;
            _ctx.ElapsedTime += dt;
            _ctx.Fps = dt > 0.0001f ? 1.0f / dt : 0;
            _world.Update(dt);

            // 平滑 FPS
            _smoothedFps = _smoothedFps * 0.9f + _ctx.Fps * 0.1f;

            // ── 处理快捷键 ──
            Shortcuts.Update(_ctx);

            // ── 同步 Gizmo 捕捉设置 ──
            GizmoManager.SyncSnapSettings(Toolbar.Snap);

            // ── 同步视口设置 ──
            Viewport.Overlay.ShowGrid = Toolbar.ShowGrid;
            Viewport.Overlay.GridSize = Toolbar.GridSize;

            // ── 更新视口 ──
            Viewport.Update(_input, dt);

            // ── Gizmo 后处理 (捕捉 + 撤销记录) ──
            GizmoManager.PostUpdate(_ctx, Viewport.Camera, Viewport.Size);

            // ── 渲染视口到纹理 ──
            try { Viewport.Render(dt); }
            catch (Exception ex) { Console.AddLog(LogLevel.Error, $"视口渲染失败: {ex.Message}", "viewport"); }

            // ── 更新统计 ──
            UpdateStats(dt);

            // ── 绘制 UI ──
            DrawMenuBar();
            DrawDockSpaceAndPanels();
            DrawStatusBar();
            DrawPopups();
        }

        // ══════════════════════════ 菜单栏 ══════════════════════════

        private void DrawMenuBar()
        {
            if (!ImGui.BeginMainMenuBar()) return;

            DrawFileMenu();
            DrawEditMenu();
            DrawModeMenu();
            DrawViewMenu();
            DrawWindowMenu();
            DrawHelpMenu();

            ImGui.EndMainMenuBar();
        }

        private void DrawFileMenu()
        {
            if (!ImGui.BeginMenu("文件")) return;

            if (ImGui.MenuItem("新建场景", "Ctrl+Shift+N"))
            {
                _world.SceneManager.NewScene("Untitled");
                Console.AddLog(LogLevel.Info, "已创建新场景", "scene");
            }

            if (ImGui.MenuItem("打开场景..."))
                _showOpenScene = true;

            if (ImGui.MenuItem("保存场景", "Ctrl+S"))
            {
                if (_world.SceneManager.SaveScene())
                    Console.AddLog(LogLevel.Info, "场景已保存", "scene");
                else
                    Console.AddLog(LogLevel.Warning, "保存场景失败", "scene");
            }

            if (ImGui.MenuItem("另存为..."))
                _showSaveAs = true;

            ImGui.Separator();

            if (ImGui.MenuItem("导入资源..."))
                _showImport = true;

            if (ImGui.MenuItem("新建脚本..."))
            {
                CodeEditor.IsVisible = true;
                if (CodeEditor.IsClosed) CodeEditor.IsClosed = false;
            }

            if (ImGui.MenuItem("打开代码编辑器"))
            {
                CodeEditor.IsVisible = true;
                if (CodeEditor.IsClosed) CodeEditor.IsClosed = false;
            }

            ImGui.Separator();

            if (ImGui.MenuItem("保存布局"))
                SaveLayout();

            if (ImGui.MenuItem("加载布局"))
                LoadLayout();

            if (ImGui.MenuItem("重置布局"))
                _firstLayout = true;

            ImGui.Separator();

            if (ImGui.MenuItem("退出"))
                Environment.Exit(0);

            ImGui.EndMenu();
        }

        private void DrawEditMenu()
        {
            if (!ImGui.BeginMenu("编辑")) return;

            if (ImGui.MenuItem("撤销", "Ctrl+Z", false, _world.UndoStack.CanUndo))
                _world.Undo();

            if (ImGui.MenuItem("重做", "Ctrl+Y", false, _world.UndoStack.CanRedo))
                _world.Redo();

            ImGui.Separator();

            if (ImGui.MenuItem("复制", "Ctrl+C"))
                _world.Copy();

            if (ImGui.MenuItem("粘贴", "Ctrl+V"))
                _world.Paste();

            if (ImGui.MenuItem("剪切", "Ctrl+X"))
                _world.Cut();

            if (ImGui.MenuItem("复制对象", "Ctrl+D"))
                _world.Duplicate();

            if (ImGui.MenuItem("删除", "Del"))
                _world.Delete();

            ImGui.Separator();

            if (ImGui.MenuItem("全选", "Ctrl+A"))
                _world.SelectAll();

            if (ImGui.MenuItem("取消选择"))
                _world.Selection.Clear();

            ImGui.Separator();

            // 对齐工具
            if (ImGui.BeginMenu("对齐"))
            {
                if (ImGui.MenuItem("对齐 X (最小)"))
                    TransformTools.AlignX(_world.Selection, AlignMode.Min, _world.UndoStack);
                if (ImGui.MenuItem("对齐 Y (最小)"))
                    TransformTools.AlignY(_world.Selection, AlignMode.Min, _world.UndoStack);
                if (ImGui.MenuItem("对齐 Z (最小)"))
                    TransformTools.AlignZ(_world.Selection, AlignMode.Min, _world.UndoStack);
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("重置变换"))
                TransformTools.ResetTransform(_world.Selection, _world.UndoStack);

            ImGui.EndMenu();
        }

        // ══════════════════════════ 模式菜单 ══════════════════════════

        private void DrawModeMenu()
        {
            if (!ImGui.BeginMenu("模式")) return;

            foreach (EditorMode mode in System.Enum.GetValues(typeof(EditorMode)))
            {
                var info = UnifiedEditorModeSystem.GetModeInfo(mode);
                bool active = ModeSystem.CurrentMode == mode;
                if (ImGui.MenuItem($"{info.Name} [{info.Shortcut}]", null, active))
                    ModeSystem.SwitchTo(mode);
            }

            ImGui.Separator();

            if (ImGui.MenuItem("返回上一模式", "Tab"))
                ModeSystem.SwitchBack();

            ImGui.EndMenu();
        }

        private void DrawViewMenu()
        {
            if (!ImGui.BeginMenu("视图")) return;

            // 渲染模式
            if (ImGui.BeginMenu("渲染模式"))
            {
                string[] modes = { "Lit", "Unlit", "Wireframe", "Albedo", "Normal", "Roughness", "Metallic", "Emission", "Depth", "Velocity" };
                foreach (var m in modes)
                {
                    if (ImGui.MenuItem(m, null, Toolbar.RenderMode.ToString() == m))
                        Toolbar.RenderMode = (ViewportRenderMode)Enum.Parse(typeof(ViewportRenderMode), m);
                }
                ImGui.EndMenu();
            }

            ImGui.Separator();

            // 变换模式
            if (ImGui.MenuItem("移动 (W)", null, GizmoManager.Mode == GizmoMode.Move))
                GizmoManager.SetMode(GizmoMode.Move);
            if (ImGui.MenuItem("旋转 (E)", null, GizmoManager.Mode == GizmoMode.Rotate))
                GizmoManager.SetMode(GizmoMode.Rotate);
            if (ImGui.MenuItem("缩放 (R)", null, GizmoManager.Mode == GizmoMode.Scale))
                GizmoManager.SetMode(GizmoMode.Scale);

            ImGui.Separator();

            if (ImGui.MenuItem("世界坐标系", null, GizmoManager.UseWorldSpace))
                GizmoManager.UseWorldSpace = !GizmoManager.UseWorldSpace;

            bool showGrid = Toolbar.ShowGrid;
            bool showOverlay = Toolbar.ShowStatsOverlay;
            ImGui.MenuItem("显示网格", null, ref showGrid);
            ImGui.MenuItem("显示统计覆盖层", null, ref showOverlay);
            Toolbar.ShowGrid = showGrid;
            Toolbar.ShowStatsOverlay = showOverlay;

            ImGui.Separator();

            // 视口布局
            if (ImGui.BeginMenu("视口布局"))
            {
                if (ImGui.MenuItem("单视图", null, Viewport.Layout == ViewportLayout.SinglePerspective))
                    Viewport.ApplyLayout(ViewportLayout.SinglePerspective);
                if (ImGui.MenuItem("四视图", null, Viewport.Layout == ViewportLayout.Quad))
                    Viewport.ApplyLayout(ViewportLayout.Quad);
                if (ImGui.MenuItem("左右分屏", null, Viewport.Layout == ViewportLayout.Split2))
                    Viewport.ApplyLayout(ViewportLayout.Split2);
                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }

        private void DrawWindowMenu()
        {
            if (!ImGui.BeginMenu("窗口")) return;

            DrawPanelToggle("大纲", Outliner);
            DrawPanelToggle("检查器", Inspector);
            DrawPanelToggle("内容浏览器", ContentBrowser);
            DrawPanelToggle("工具栏", Toolbar);
            DrawPanelToggle("控制台", Console);
            DrawPanelToggle("统计", Stats);
            DrawPanelToggle("代码编辑器", CodeEditor);
            DrawPanelToggle("地形编辑器", TerrainEditor);
            DrawPanelToggle("建模工具", ModelingTool);

            ImGui.Separator();

            if (ImGui.MenuItem("关闭所有面板"))
                foreach (var p in _allPanels) { if (p.CanClose) p.IsVisible = false; }

            if (ImGui.MenuItem("打开所有面板"))
                foreach (var p in _allPanels) p.IsVisible = true;

            ImGui.EndMenu();
        }

        private void DrawPanelToggle(string label, IEditorPanel panel)
        {
            bool visible = panel.IsVisible;
            if (ImGui.MenuItem(label, null, visible))
                panel.IsVisible = !visible;
            if (panel.IsClosed && visible)
                panel.IsClosed = false;
        }

        private void DrawHelpMenu()
        {
            if (!ImGui.BeginMenu("帮助")) return;

            if (ImGui.MenuItem("快捷键"))
                _showShortcuts = true;

            if (ImGui.MenuItem("关于yama引擎"))
                _showAbout = true;

            ImGui.Separator();

            if (ImGui.MenuItem("清除控制台"))
                Console.Clear();

            ImGui.EndMenu();
        }

        // ══════════════════════════ 停靠空间与面板 ══════════════════════════

        private void DrawDockSpaceAndPanels()
        {
            const float StatusBarHeight = 26;

            var vp = ImGui.GetMainViewport();
            var workPos = vp.WorkPos;
            var workSize = vp.WorkSize;

            // ── 主停靠宿主窗口 ──
            ImGui.SetNextWindowPos(workPos);
            ImGui.SetNextWindowSize(new Vector2(workSize.X, workSize.Y - StatusBarHeight));

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            ImGuiWindowFlags hostFlags =
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoNavFocus;

            ImGui.Begin("###MainDockSpaceHost", hostFlags);
            ImGui.PopStyleVar(3);

            // 创建 DockSpace
            uint dockspaceId = ImGui.GetID("MainDockSpace");
            ImGui.DockSpace(dockspaceId, new Vector2(0, 0), ImGuiDockNodeFlags.PassthruCentralNode);

            // 首次布局
            if (_firstLayout)
            {
                ApplyDefaultLayout(dockspaceId);
                _firstLayout = false;
            }

            // 渲染所有面板
            foreach (var panel in _allPanels)
                panel.Render(_ctx);

            // 渲染视口窗口
            DrawViewportWindow();

            ImGui.End();
        }

        // ══════════════════════════ 视口窗口 ══════════════════════════

        private void DrawViewportWindow()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            bool open = true;
            if (!ImGui.Begin("视口###Viewport", ref open,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.End();
                ImGui.PopStyleVar();
                return;
            }
            ImGui.PopStyleVar();

            var avail = ImGui.GetContentRegionAvail();
            int w = (int)avail.X, h = (int)avail.Y;

            if (w > 0 && h > 0)
            {
                // 更新视口尺寸
                Viewport.Size = avail;
                if (w != _lastViewportW || h != _lastViewportH)
                {
                    Viewport.Resize(w, h);
                    CreateViewportSrv();
                    _lastViewportW = w;
                    _lastViewportH = h;
                }

                // 设置焦点状态
                Viewport.IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
            }

            // 显示渲染纹理
            if (_viewportSrvValid && _viewportTextureId != IntPtr.Zero)
            {
                ImGui.Image(_viewportTextureId, avail);
            }
            else
            {
                // SRV 未就绪, 显示占位
                var pos = ImGui.GetCursorScreenPos();
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(pos, pos + avail, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.14f, 0.18f, 1f)));
                ImGui.SetCursorPos(ImGui.GetCursorPos() + avail * 0.5f - new Vector2(80, 12));
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.55f, 1f), "视口渲染中...");
                ImGui.Dummy(avail);
            }

            // ── 拾取 ──
            HandleViewportPicking();

            // ── 资产拖放 ──
            HandleViewportDragDrop();

            // ── 统计覆盖层 ──
            if (Toolbar.ShowStatsOverlay)
                DrawViewportOverlay();

            // ── PIE 状态覆盖层 ──
            DrawPieOverlay();

            ImGui.End();
        }

        private void HandleViewportPicking()
        {
            if (!Viewport.IsFocused) return;
            if (!_input.IsKeyPressed(InputKey.MouseLeft)) return;
            if (ImGui.GetIO().WantCaptureMouse && !ImGui.IsItemHovered()) return;

            // 检查是否点击在 Gizmo 上
            var selection = _world.Selection.Selected.ToList();
            bool overGizmo = GizmoManager.IsMouseOverGizmo(
                _input.MousePosition - ImGui.GetItemRectMin(),
                Viewport.Camera, Viewport.Size, selection);

            if (!overGizmo)
            {
                var localMouse = ImGui.GetMousePos() - ImGui.GetItemRectMin();
                bool additive = _input.IsKeyDown(InputKey.Control);
                GizmoManager.PickAndSelect(localMouse, Viewport.Camera, Viewport.Size,
                    _world.Selection, additive);
            }
        }

        private unsafe void HandleViewportDragDrop()
        {
            if (!ImGui.BeginDragDropTarget()) return;

            var payload = ImGui.AcceptDragDropPayload("CONTENT_ASSET");
            if (payload.NativePtr != null)
            {
                string path = ContentBrowser.DraggingAssetPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var dropPos = ImGui.GetMousePos() - ImGui.GetItemRectMin();
                    Viewport.HandleDropAsset(path, dropPos);
                    Console.AddLog(LogLevel.Info, $"已拖放资产到视口: {path}", "viewport");
                }
            }
            ImGui.EndDragDropTarget();
        }

        private void DrawViewportOverlay()
        {
            const float pad = 10;
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();

            // 左上角: FPS + 渲染信息
            ImGui.SetNextWindowPos(new Vector2(pos.X + pad, pos.Y + pad));
            ImGui.SetNextWindowBgAlpha(0.7f);
            if (ImGui.Begin("###ViewportStats",
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
            {
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), $"{_smoothedFps:F1} FPS");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"{_ctx.DeltaTime * 1000:F2} ms");
                ImGui.Separator();
                ImGui.Text($"实体: {_assets.ObjectCount}");
                ImGui.Text($"DrawCall: {_drawCallStats.Total}");
                ImGui.Text($"三角形: {_drawCallStats.Triangles:N0}");
                ImGui.Text($"模式: {Toolbar.RenderMode}");
                ImGui.Text($"Gizmo: {GizmoManager.Mode}");
                ImGui.Text($"坐标: {(GizmoManager.UseWorldSpace ? "世界" : "本地")}");
                ImGui.End();
            }

            // 右上角: 摄像机信息
            ImGui.SetNextWindowPos(new Vector2(pos.X + size.X - 200, pos.Y + pad));
            ImGui.SetNextWindowBgAlpha(0.7f);
            if (ImGui.Begin("###ViewportCam",
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
            {
                var cam = Viewport.Camera;
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), "摄像机");
                ImGui.Text($"位置: ({cam.Position.X:F1}, {cam.Position.Y:F1}, {cam.Position.Z:F1})");
                ImGui.Text($"模式: {cam.Mode}");
                ImGui.Text($"FOV: {cam.FieldOfView:F0}°");
                ImGui.End();
            }
        }

        private void DrawPieOverlay()
        {
            if (_world.Pie.State == PieState.Editing) return;

            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            const float barH = 28;

            ImGui.SetNextWindowPos(new Vector2(pos.X, pos.Y + size.Y - barH));
            ImGui.SetNextWindowSize(new Vector2(size.X, barH));
            ImGui.SetNextWindowBgAlpha(0.85f);

            if (ImGui.Begin("###PieBar",
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav))
            {
                string state = _world.Pie.IsPlaying ? "▶ 运行中" : "⏸ 已暂停";
                var color = _world.Pie.IsPlaying
                    ? new Vector4(0.3f, 0.7f, 0.4f, 1f)
                    : new Vector4(0.95f, 0.75f, 0.2f, 1f);
                ImGui.TextColored(color, $"  {state}");

                ImGui.SameLine(size.X - 300);
                if (ImGui.Button("停止"))
                    _world.Stop();
                ImGui.SameLine();
                if (ImGui.Button(_world.Pie.IsPlaying ? "暂停" : "恢复"))
                    _world.TogglePause();
                ImGui.SameLine();
                if (ImGui.Button("单步"))
                    _world.Pie.StepFrame();

                ImGui.End();
            }
        }

        // ══════════════════════════ 状态栏 ══════════════════════════

        /// <summary>绘制底部状态栏窗口及其内容。</summary>
        private void DrawStatusBar()
        {
            const float StatusBarHeight = 26;

            var vp = ImGui.GetMainViewport();
            var workPos = vp.WorkPos;
            var workSize = vp.WorkSize;

            ImGui.SetNextWindowPos(new Vector2(workPos.X, workPos.Y + workSize.Y - StatusBarHeight));
            ImGui.SetNextWindowSize(new Vector2(workSize.X, StatusBarHeight));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 4));
            ImGui.Begin("###StatusBar",
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoNavFocus);
            ImGui.PopStyleVar(3);
            DrawStatusBarContent();
            ImGui.End();
        }

        private void DrawStatusBarContent()
        {
            // 左侧: 场景名 + 模式 + 选中信息
            string sceneName = _world.SceneManager?.CurrentSceneName ?? "Untitled";
            bool dirty = _world.SceneManager?.IsDirty ?? false;
            ImGui.TextColored(dirty ? new Vector4(0.95f, 0.75f, 0.2f, 1f) : new Vector4(0.7f, 0.7f, 0.75f, 1f),
                $"  场景: {sceneName}{(dirty ? " *" : "")}");

            // 当前编辑器模式
            var modeInfo = ModeSystem.CurrentModeInfo;
            ImGui.SameLine(250);
            ImGui.TextColored(modeInfo.Color, $"{modeInfo.Icon} {modeInfo.ShortName}");

            int selCount = _world.Selection.Count;
            if (selCount > 0)
            {
                ImGui.SameLine(350);
                var active = _world.Selection.Active;
                string selInfo = selCount == 1 && active != null
                    ? $"选中: {active.Name}"
                    : $"选中: {selCount} 个对象";
                ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.95f, 1f), selInfo);
            }

            // 右侧: 性能信息
            var fpsColor = _smoothedFps >= 55 ? new Vector4(0.3f, 0.85f, 0.4f, 1f)
                        : _smoothedFps >= 30 ? new Vector4(0.95f, 0.75f, 0.2f, 1f)
                        : new Vector4(0.9f, 0.3f, 0.3f, 1f);

            ImGui.SameLine(ImGui.GetWindowWidth() - 480);
            ImGui.TextColored(fpsColor, $"FPS: {_smoothedFps:F1}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"| {_ctx.DeltaTime * 1000:F2}ms");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"| 实体: {_assets.ObjectCount}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"| DC: {_drawCallStats.Total}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"| Tri: {_drawCallStats.Triangles:N0}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), $"| {FormatBytes(_memoryStats.TotalBytes)}");

            // 撤销/重做指示
            if (_world.UndoStack.CanUndo || _world.UndoStack.CanRedo)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.55f, 1f),
                    $"| U:{_world.UndoStack.UndoCount} R:{_world.UndoStack.RedoCount}");
            }
        }

        // ══════════════════════════ 弹窗 ══════════════════════════

        private void DrawPopups()
        {
            if (_showOpenScene) { ImGui.OpenPopup("打开场景###OpenScenePopup"); _showOpenScene = false; }
            if (_showSaveAs) { ImGui.OpenPopup("另存为###SaveAsPopup"); _showSaveAs = false; }
            if (_showAbout) { ImGui.OpenPopup("关于###AboutPopup"); _showAbout = false; }
            if (_showShortcuts) { ImGui.OpenPopup("快捷键###ShortcutsPopup"); _showShortcuts = false; }
            if (_showImport) { ImGui.OpenPopup("导入资源###ImportPopup"); _showImport = false; }

            DrawOpenScenePopup();
            DrawSaveAsPopup();
            DrawImportPopup();
            DrawAboutPopup();
            DrawShortcutsPopup();
        }

        private void DrawOpenScenePopup()
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (!ImGui.BeginPopupModal("打开场景###OpenScenePopup")) return;

            ImGui.Text("输入场景文件路径:");
            ImGui.InputText("##path", ref _scenePathInput, 256);
            ImGui.SameLine();
            if (ImGui.Button("浏览"))
            {
                // 简化: 列出 Assets 目录下的 .json 文件
                try
                {
                    var dir = _assets.RootPath ?? ".";
                    var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
                    if (files.Length > 0) _scenePathInput = files[0];
                }
                catch { }
            }

            if (ImGui.Button("打开", new Vector2(80, 0)))
            {
                if (_world.SceneManager.LoadScene(_scenePathInput))
                    Console.AddLog(LogLevel.Info, $"已加载场景: {_scenePathInput}", "scene");
                else
                    Console.AddLog(LogLevel.Error, $"加载场景失败: {_scenePathInput}", "scene");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(80, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private void DrawSaveAsPopup()
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (!ImGui.BeginPopupModal("另存为###SaveAsPopup")) return;

            ImGui.Text("保存到:");
            ImGui.InputText("##savepath", ref _scenePathInput, 256);

            if (ImGui.Button("保存", new Vector2(80, 0)))
            {
                if (_world.SceneManager.SaveSceneAs(_scenePathInput))
                    Console.AddLog(LogLevel.Info, $"场景已保存到: {_scenePathInput}", "scene");
                else
                    Console.AddLog(LogLevel.Error, "保存场景失败", "scene");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(80, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private void DrawImportPopup()
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (!ImGui.BeginPopupModal("导入资源###ImportPopup")) return;

            ImGui.Text("源文件路径:");
            ImGui.InputText("##importpath", ref _importPathInput, 512);

            ImGui.Text("资产类型:");
            ImGui.SameLine();
            int typeIdx = 0;
            string[] typeNames = { "Unknown", "Mesh", "Texture", "Material", "Shader", "Audio", "Scene", "NodeScript", "Font" };
            ImGui.Combo("##importtype", ref typeIdx, typeNames, typeNames.Length);

            if (ImGui.Button("导入", new Vector2(80, 0)))
            {
                if (!string.IsNullOrEmpty(_importPathInput))
                {
                    var type = (AssetType)typeIdx;
                    var meta = _assets.ImportAsset(_importPathInput, type);
                    if (meta != null)
                        Console.AddLog(LogLevel.Info, $"已导入: {meta.Name} ({meta.Guid})", "import");
                    else
                        Console.AddLog(LogLevel.Warning, "导入返回空", "import");
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(80, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private void DrawAboutPopup()
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (!ImGui.BeginPopupModal("关于###AboutPopup",
                ImGuiWindowFlags.AlwaysAutoResize)) return;

            ImGui.Spacing();
            ImGui.PushFont(ImGui.GetFont());
            ImGui.SetWindowFontScale(1.5f);
            ImGui.TextColored(new Vector4(0.26f, 0.59f, 0.98f, 1f), "  yama游戏引擎");
            ImGui.SetWindowFontScale(1f);
            ImGui.PopFont();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("Yama Game Engine & Modeling");
            ImGui.Text("版本: 1.0.0");
            ImGui.Text($"ImGui: {GetImGuiVersion()}");
            ImGui.Text($".NET: {Environment.Version}");
            ImGui.Spacing();
            ImGui.TextWrapped("一个面向 LuckyOS 的游戏开发与建模引擎, 支持 D3D12 / Vulkan 后端, ECS 架构, PBR 渲染, 前沿编辑器。");
            ImGui.Spacing();

            if (ImGui.Button("确定", new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private void DrawShortcutsPopup()
        {
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(500, 500), ImGuiCond.Appearing);
            if (!ImGui.BeginPopupModal("快捷键###ShortcutsPopup")) return;

            ImGui.Text("快捷键列表");
            ImGui.Separator();

            if (ImGui.BeginChild("##shortcut_list", new Vector2(-1, -40)))
            {
                foreach (var group in Shortcuts.GetByCategory())
                {
                    if (ImGui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        foreach (var sc in group)
                        {
                            ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.95f, 1f), $"  {sc.DisplayString,-25}");
                            ImGui.SameLine(200);
                            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f), sc.Description);
                        }
                    }
                }
                ImGui.EndChild();
            }

            if (ImGui.Button("关闭", new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        // ══════════════════════════ 默认布局 ══════════════════════════

        private void ApplyDefaultLayout(uint dockspaceId)
        {
            try
            {
                // 当前 ImGui.NET 绑定版本未启用 DockBuilder 停靠构建接口
                // (无 DockBuilder* / ImGuiID / ImGuiDockNodeFlags.DockSpace),
                // 因此这里不执行程序化停靠布局, 各面板以其默认窗口浮动显示,
                // 停靠宿主仍由上面的 ImGui.DockSpace 提供。
                // 后续若启用完整 Docking 后端, 可在此恢复 DockBuilder 布局编排。
                Console.AddLog(LogLevel.Debug, "已应用默认布局 (基础模式)", "layout");
            }
            catch (Exception ex)
            {
                Console.AddLog(LogLevel.Warning, $"布局初始化失败: {ex.Message}", "layout");
            }
        }

        // ══════════════════════════ 布局持久化 ══════════════════════════

        public void SaveLayout()
        {
            try
            {
                string path = Path.Combine(_assets.RootPath ?? ".", _layoutPath);
                DockSpace.SaveLayout(path);
                Console.AddLog(LogLevel.Info, $"布局已保存: {path}", "layout");
            }
            catch (Exception ex)
            {
                Console.AddLog(LogLevel.Error, $"保存布局失败: {ex.Message}", "layout");
            }
        }

        public void LoadLayout()
        {
            try
            {
                string path = Path.Combine(_assets.RootPath ?? ".", _layoutPath);
                if (File.Exists(path))
                {
                    DockSpace.LoadLayout(path);
                    Console.AddLog(LogLevel.Info, $"布局已加载: {path}", "layout");
                }
                else
                {
                    Console.AddLog(LogLevel.Warning, $"布局文件不存在: {path}", "layout");
                }
            }
            catch (Exception ex)
            {
                Console.AddLog(LogLevel.Error, $"加载布局失败: {ex.Message}", "layout");
            }
        }

        // ══════════════════════════ 视口纹理 ══════════════════════════

        private void CreateViewportSrv()
        {
            try
            {
                if (Viewport?.RenderTarget?.ColorTexture != null && _device != null)
                {
                    _viewportSrv = _device.CreateSRV(Viewport.RenderTarget.ColorTexture);
                    _viewportTextureId = (IntPtr)(long)_viewportSrv.GpuHandle;
                    _viewportSrvValid = true;
                }
            }
            catch (NotSupportedException)
            {
                _viewportSrvValid = false;
            }
            catch (Exception)
            {
                _viewportSrvValid = false;
            }
        }

        // ══════════════════════════ 统计更新 ══════════════════════════

        private void UpdateStats(float dt)
        {
            _frameTimeAccum += dt;
            _frameCount++;

            // 模拟帧时间分解 (真实值由引擎填充)
            _frameBreakdown = new FrameTimeBreakdown
            {
                UpdateMs = dt * 1000 * 0.3f,
                RenderMs = dt * 1000 * 0.5f,
                PhysicsMs = dt * 1000 * 0.1f,
                AudioMs = dt * 1000 * 0.02f,
                GcMs = dt * 1000 * 0.08f
            };

            // 每 0.5 秒更新一次内存统计 (避免频繁 GC 查询)
            _statsUpdateTimer += dt;
            if (_statsUpdateTimer >= 0.5f)
            {
                _statsUpdateTimer = 0;
                _memoryStats = new MemoryStats
                {
                    ManagedBytes = GC.GetTotalMemory(false),
                    GpuBytes = 0,
                    TextureBytes = 0,
                    MeshBytes = 0
                };
            }

            // DrawCall 统计 (真实值由渲染器填充)
            _drawCallStats = new DrawCallStats
            {
                Total = _assets.ObjectCount,
                Opaque = _assets.ObjectCount,
                Transparent = 0,
                Shadow = 0,
                Triangles = _assets.ObjectCount * 1200
            };

            // 每帧更新统计面板 (FPS 历史需要每帧采样)
            Stats.UpdateStats(_smoothedFps, _ctx.DeltaTime * 1000, _frameBreakdown, _memoryStats, _drawCallStats);
        }

        // ══════════════════════════ 命令注册 ══════════════════════════

        private void RegisterCommands()
        {
            Commands.Register("help", "显示可用命令", (args, ctx) =>
            {
                foreach (var (name, desc) in Commands.GetCommands())
                    ctx.Console.AddLog(LogLevel.Info, $"  {name,-15} {desc}", "cmd");
            });

            Commands.Register("clear", "清除控制台", (args, ctx) => ctx.Console?.Clear());

            Commands.Register("save", "保存场景", (args, ctx) =>
            {
                ctx.SceneManager?.SaveScene();
                ctx.Console?.AddLog(LogLevel.Info, "场景已保存", "cmd");
            });

            Commands.Register("load", "加载场景 (load <path>)", (args, ctx) =>
            {
                if (args.Length > 0)
                {
                    if (ctx.SceneManager?.LoadScene(args[0]) == true)
                        ctx.Console?.AddLog(LogLevel.Info, $"已加载: {args[0]}", "cmd");
                    else
                        ctx.Console?.AddLog(LogLevel.Error, $"加载失败: {args[0]}", "cmd");
                }
                else ctx.Console?.AddLog(LogLevel.Warning, "用法: load <path>", "cmd");
            });

            Commands.Register("new", "新建场景", (args, ctx) =>
            {
                string name = args.Length > 0 ? args[0] : "Untitled";
                ctx.SceneManager?.NewScene(name);
                ctx.Console?.AddLog(LogLevel.Info, $"已创建场景: {name}", "cmd");
            });

            Commands.Register("play", "开始 PIE", (args, ctx) => ctx.World?.Play());
            Commands.Register("stop", "停止 PIE", (args, ctx) => ctx.World?.Stop());
            Commands.Register("pause", "暂停 PIE", (args, ctx) => ctx.World?.TogglePause());

            Commands.Register("undo", "撤销", (args, ctx) => ctx.World?.Undo());
            Commands.Register("redo", "重做", (args, ctx) => ctx.World?.Redo());

            Commands.Register("delete", "删除选中", (args, ctx) => ctx.World?.Delete());
            Commands.Register("duplicate", "复制选中", (args, ctx) => ctx.World?.Duplicate());

            Commands.Register("create", "创建对象 (create <name> [type])", (args, ctx) =>
            {
                string name = args.Length > 0 ? args[0] : "GameObject";
                string type = args.Length > 1 ? args[1] : "Box";
                var obj = ctx.SceneManager?.CreateObject(name, type, Vector3.Zero);
                if (obj != null)
                {
                    ctx.Selection?.Select(obj);
                    ctx.Console?.AddLog(LogLevel.Info, $"已创建: {name} ({type})", "cmd");
                }
            });

            Commands.Register("select", "选择对象 (select <name>)", (args, ctx) =>
            {
                if (args.Length > 0 && ctx.Assets != null)
                {
                    var obj = ctx.Assets.SceneObjects.FirstOrDefault(o => o.Name == args[0]);
                    if (obj != null) ctx.Selection?.Select(obj);
                    else ctx.Console?.AddLog(LogLevel.Warning, $"未找到: {args[0]}", "cmd");
                }
            });

            Commands.Register("fps", "显示 FPS", (args, ctx) =>
                ctx.Console?.AddLog(LogLevel.Info, $"FPS: {_smoothedFps:F1}, 帧时间: {_ctx.DeltaTime * 1000:F2}ms", "cmd"));

            Commands.Register("stats", "显示统计", (args, ctx) =>
            {
                ctx.Console?.AddLog(LogLevel.Info, $"实体: {ctx.Assets?.ObjectCount ?? 0}", "cmd");
                ctx.Console?.AddLog(LogLevel.Info, $"DrawCall: {_drawCallStats.Total}", "cmd");
                ctx.Console?.AddLog(LogLevel.Info, $"内存: {FormatBytes(_memoryStats.TotalBytes)}", "cmd");
            });

            Commands.Register("list", "列出所有对象", (args, ctx) =>
            {
                if (ctx.Assets == null) return;
                foreach (var o in ctx.Assets.SceneObjects)
                    ctx.Console?.AddLog(LogLevel.Info, $"  [{o.Id:B}] {o.Name} ({o.MeshAssetPath})", "cmd");
            });

            Commands.Register("gizmo", "设置 Gizmo 模式 (gizmo <move|rotate|scale>)", (args, ctx) =>
            {
                if (args.Length > 0)
                {
                    var mode = args[0].ToLowerInvariant() switch
                    {
                        "move" or "translate" => GizmoMode.Move,
                        "rotate" => GizmoMode.Rotate,
                        "scale" => GizmoMode.Scale,
                        "none" => GizmoMode.None,
                        _ => GizmoMode.None
                    };
                    GizmoManager.SetMode(mode);
                    ctx.Console?.AddLog(LogLevel.Info, $"Gizmo: {mode}", "cmd");
                }
            });

            Commands.Register("theme", "切换主题 (theme <dark|light>)", (args, ctx) =>
            {
                if (args.Length > 0)
                {
                    var preset = args[0].ToLowerInvariant() == "light"
                        ? EditorThemePreset.Light : EditorThemePreset.Dark;
                    ctx.Theme?.ApplyPreset(preset);
                    ctx.Theme?.ApplyToImGuiStyle();
                    ctx.Console?.AddLog(LogLevel.Info, $"主题: {preset}", "cmd");
                }
            });

            Commands.Register("code", "打开代码编辑器", (args, ctx) =>
            {
                CodeEditor.IsVisible = true;
                if (CodeEditor.IsClosed) CodeEditor.IsClosed = false;
                ctx.Console?.AddLog(LogLevel.Info, "已打开代码编辑器", "cmd");
            });

            Commands.Register("terrain", "打开地形编辑器", (args, ctx) =>
            {
                TerrainEditor.IsVisible = true;
                if (TerrainEditor.IsClosed) TerrainEditor.IsClosed = false;
                ModeSystem.SwitchTo(EditorMode.Terrain);
                ctx.Console?.AddLog(LogLevel.Info, "已打开地形编辑器", "cmd");
            });

            Commands.Register("model", "打开建模工具", (args, ctx) =>
            {
                ModelingTool.IsVisible = true;
                if (ModelingTool.IsClosed) ModelingTool.IsClosed = false;
                ModeSystem.SwitchTo(EditorMode.Modeling);
                ctx.Console?.AddLog(LogLevel.Info, "已打开建模工具", "cmd");
            });

            Commands.Register("mode", "切换编辑器模式 (mode <scene|terrain|modeling|code|test>)", (args, ctx) =>
            {
                if (args.Length > 0)
                {
                    var mode = args[0].ToLowerInvariant() switch
                    {
                        "scene" or "1" => EditorMode.Scene,
                        "terrain" or "2" => EditorMode.Terrain,
                        "modeling" or "model" or "3" => EditorMode.Modeling,
                        "code" or "4" => EditorMode.Code,
                        "test" or "5" => EditorMode.Test,
                        _ => EditorMode.Scene
                    };
                    ModeSystem.SwitchTo(mode);
                    ctx.Console?.AddLog(LogLevel.Info, $"模式: {mode}", "cmd");
                }
                else
                {
                    ctx.Console?.AddLog(LogLevel.Info, $"当前模式: {ModeSystem.CurrentMode}", "cmd");
                }
            });

            Console.AddLog(LogLevel.Debug, $"已注册 {Commands.GetCommandNames().Count()} 个命令", "cmd");
        }

        // ══════════════════════════ 工具 ══════════════════════════

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static string GetImGuiVersion()
        {
            try { return ImGui.GetVersion(); }
            catch { return "unknown"; }
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            Viewport?.Dispose();
        }
    }
}
