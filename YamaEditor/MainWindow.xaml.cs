using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YamaEditor.Controls;
using YamaEditor.Pages;
using YamaEngine.Core;
using Windows.System;

namespace YamaEditor
{
    /// <summary>
    /// Yama Engine Editor 主窗口 —— 精简代码隐藏。
    /// 负责：窗口初始化、菜单命令分发、工具栏事件、状态栏更新、引擎 Tick 循环。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly DispatcherTimer _engineTick = new();
        private readonly DispatcherTimer _statusTick = new();
        private DateTime _lastFrameTime = DateTime.Now;
        private int _windowPointerId; // 用于标题栏拖拽

        public MainWindow()
        {
            this.InitializeComponent();

            // 窗口初始化
            AppWindow.Title = "Yama Engine - Industrial Game Development Platform";
            // WinUI 3 的 Window 无 Width/Height 属性，尺寸经 AppWindow 设置
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 900));
            ExtendsContentIntoTitleBar = true; // XBF 不识别 XAML 属性，改在代码设置
            SetTitleBar(TitleBar);

            // 引擎 Tick 循环（约 60fps）
            _engineTick.Interval = TimeSpan.FromMilliseconds(16);
            _engineTick.Tick += OnEngineTick;

            // 状态栏刷新（每 250ms）
            _statusTick.Interval = TimeSpan.FromMilliseconds(250);
            _statusTick.Tick += OnStatusTick;

            // 键盘快捷键（WinUI 3 的 Window 无 KeyDown 事件，挂到根 Grid）
            RootGrid.KeyDown += OnKeyDown;

            // 窗口关闭时清理
            this.Closed += OnWindowClosed;

            // 启动定时器
            _engineTick.Start();
            _statusTick.Start();

            App.Systems?.Log("Editor UI initialized.");
        }

        // ══════════════════════════════════════════════════════════
        //  标题栏拖拽
        // ══════════════════════════════════════════════════════════
        private void OnTitleBarPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 标题栏拖拽由 SetTitleBar 自动处理，此处不做额外操作
        }

        // ══════════════════════════════════════════════════════════
        //  引擎 Tick
        // ══════════════════════════════════════════════════════════
        private void OnEngineTick(object sender, object e)
        {
            var now = DateTime.Now;
            float dt = (float)(now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

            // 限制 dt 防止大跳帧
            dt = Math.Min(dt, 0.1f);

            try
            {
                var systems = App.Systems;
                if (systems != null)
                {
                    systems.Tick(dt);
                    Viewport.UpdateCameraMovement(dt);
                    Viewport.RenderFrame();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] Engine tick error: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  状态栏更新
        // ══════════════════════════════════════════════════════════
        private void OnStatusTick(object sender, object e)
        {
            var systems = App.Systems;
            if (systems == null) return;

            // 当前工具
            StatusTool.Text = systems.Gizmo.Mode.ToString();

            // FPS
            StatusFPS.Text = $"{systems.Time.FPS:F0} FPS";

            // Draw Calls
            StatusDrawCalls.Text = $"Draw: {systems.Performance.DrawCalls}";

            // GPU
            StatusGPU.Text = $"GPU: {systems.Performance.AllocatedMemoryMB:N0} MB";

            // 选中数量
            StatusSelection.Text = $"Selected: {systems.Selection.Count}";
        }

        // ══════════════════════════════════════════════════════════
        //  菜单命令分发
        // ══════════════════════════════════════════════════════════
        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item) return;
            string? tag = item.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return;

            // 优先走 CommandRegistry，没有则本地处理
            if (App.Commands?.Execute(tag) == true) return;

            // 本地处理的命令
            HandleLocalCommand(tag);
        }

        private void HandleLocalCommand(string tag)
        {
            switch (tag)
            {
                case "Edit.SelectAll":
                    var allObjects = App.Systems?.Assets?.SceneObjects;
                    if (allObjects != null)
                        foreach (var obj in allObjects)
                            App.Systems?.Selection.Select(obj, additive: true);
                    break;

                case "Edit.DeselectAll":
                    App.Systems?.Selection.Clear();
                    break;

                case "View.ToggleGrid":
                    var systems = App.Systems;
                    if (systems != null)
                    {
                        systems.Rendering.ShowGrid = !systems.Rendering.ShowGrid;
                        Viewport.ShowGrid = systems.Rendering.ShowGrid;
                        GridToggle.IsChecked = systems.Rendering.ShowGrid;
                    }
                    break;

                case "View.ToggleGizmos":
                    if (App.Systems != null)
                    {
                        App.Systems.Rendering.ShowGizmos = !App.Systems.Rendering.ShowGizmos;
                        Viewport.ShowGizmos = App.Systems.Rendering.ShowGizmos;
                    }
                    break;

                case "View.Perspective":
                    Viewport.SetViewportMode(ViewportMode.Perspective);
                    ViewportModeCombo.SelectedIndex = 0;
                    break;

                case "View.Top":
                    Viewport.SetViewportMode(ViewportMode.Top);
                    ViewportModeCombo.SelectedIndex = 2;
                    break;

                case "View.Front":
                    Viewport.SetViewportMode(ViewportMode.Front);
                    ViewportModeCombo.SelectedIndex = 3;
                    break;

                case "View.Left":
                    Viewport.SetViewportMode(ViewportMode.Left);
                    ViewportModeCombo.SelectedIndex = 4;
                    break;

                case "Help.About":
                    ShowAboutDialog();
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  工具栏事件
        // ══════════════════════════════════════════════════════════
        private void GizmoTranslate_Click(object sender, RoutedEventArgs e)
        {
            App.State!.GizmoMode = GizmoMode.Translate;
            App.Systems!.Gizmo.Mode = GizmoMode.Translate;
            Viewport.GizmoMode = GizmoMode.Translate;
        }

        private void GizmoRotate_Click(object sender, RoutedEventArgs e)
        {
            App.State!.GizmoMode = GizmoMode.Rotate;
            App.Systems!.Gizmo.Mode = GizmoMode.Rotate;
            Viewport.GizmoMode = GizmoMode.Rotate;
        }

        private void GizmoScale_Click(object sender, RoutedEventArgs e)
        {
            App.State!.GizmoMode = GizmoMode.Scale;
            App.Systems!.Gizmo.Mode = GizmoMode.Scale;
            Viewport.GizmoMode = GizmoMode.Scale;
        }

        private void ToolbarButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? tag = btn.Tag?.ToString();
            if (!string.IsNullOrEmpty(tag))
                App.Commands?.Execute(tag);
        }

        private void OnGridToggle(object sender, RoutedEventArgs e)
        {
            if (App.Systems != null)
            {
                App.Systems.Rendering.ShowGrid = GridToggle.IsChecked ?? true;
                Viewport.ShowGrid = App.Systems.Rendering.ShowGrid;
            }
        }

        private void OnWireframeToggle(object sender, RoutedEventArgs e)
        {
            if (App.Systems != null)
            {
                App.Systems.ConsoleVariables.TrySet("r.Wireframe",
                    (WireframeToggle.IsChecked ?? false) ? "1" : "0");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  播放控制
        // ══════════════════════════════════════════════════════════
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            App.Commands?.Execute("Engine.Play");
            ViewportStatusText.Text = "Playing...";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            App.Commands?.Execute("Engine.Pause");
            ViewportStatusText.Text = App.Systems?.IsPaused == true ? "Paused" : "Playing...";
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            App.Commands?.Execute("Engine.Stop");
            ViewportStatusText.Text = "Ready";
        }

        // ══════════════════════════════════════════════════════════
        //  视口交互
        // ══════════════════════════════════════════════════════════
        private void OnViewportModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewportModeCombo.SelectedItem is not ComboBoxItem item) return;
            var modeStr = item.Content?.ToString();
            if (string.IsNullOrEmpty(modeStr)) return;

            var mode = Enum.TryParse<ViewportMode>(modeStr, out var m) ? m : ViewportMode.Perspective;
            Viewport.SetViewportMode(mode);
        }

        private void OnViewportActorClicked(object sender, ViewportClickEventArgs e)
        {
            if (e.Handled) return;
            // 射线拾取（简化版：选择最近的对象）
            var systems = App.Systems;
            if (systems?.Assets == null) return;

            // TODO: 实现基于 NormalizedX/Y 的射线拾取
            // 当前简化为循环选中
            var idx = systems.Selection.Count % systems.Assets.SceneObjects.Count;
            systems.Selection.Select(systems.Assets.SceneObjects[idx]);
        }

        // ══════════════════════════════════════════════════════════
        //  键盘快捷键
        // ══════════════════════════════════════════════════════════
        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var key = e.Key;

            // 视口 WASD 相机控制
            if (key is VirtualKey.W or VirtualKey.A or VirtualKey.S or
                VirtualKey.D or VirtualKey.Q or VirtualKey.E)
            {
                Viewport.ProcessKeyboardInput(key, true);
                return;
            }

            switch (key)
            {
                case VirtualKey.W:
                    GizmoTranslate_Click(this, new RoutedEventArgs());
                    break;
                case VirtualKey.E:
                    GizmoRotate_Click(this, new RoutedEventArgs());
                    break;
                case VirtualKey.R:
                    GizmoScale_Click(this, new RoutedEventArgs());
                    break;
                case VirtualKey.G:
                    HandleLocalCommand("View.ToggleGrid");
                    break;
                case VirtualKey.F:
                    // Focus on selected object
                    FocusSelection();
                    break;
                case VirtualKey.Delete:
                    App.Commands?.Execute("Edit.Delete");
                    OutlinerPage.Refresh();
                    break;
            }
        }

        private void FocusSelection()
        {
            var systems = App.Systems;
            if (systems == null) return;

            var active = systems.Selection.Active;
            if (active != null)
            {
                systems.ViewportCamera.Target = active.Position;
                float dist = Math.Max(2f,
                    (active.Bounds.Max - active.Bounds.Min).Length());
                systems.ViewportCamera.Distance = dist * 1.2f;
                ViewportStatusText.Text = $"Focused: {active.Name}";
            }
        }

        // ══════════════════════════════════════════════════════════
        //  About 对话框
        // ══════════════════════════════════════════════════════════
        private async void ShowAboutDialog()
        {
            var dialog = new ContentDialog
            {
                Title = "About Yama Engine",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Yama Engine v1.0", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = "Industrial Game Development Platform", FontSize = 12 },
                        new TextBlock { Text = "Built with .NET 8 + WinUI 3 + Vulkan 1.4 (Silk.NET)", FontSize = 11, Opacity = 0.7 },
                    }
                },
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        // ══════════════════════════════════════════════════════════
        //  窗口关闭
        // ══════════════════════════════════════════════════════════
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _engineTick.Stop();
            _statusTick.Stop();
            Viewport.Cleanup();
        }
    }
}
