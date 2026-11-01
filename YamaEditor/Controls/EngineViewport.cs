using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using YamaEngine.Core;
using YamaEngine.RHI;
using YamaEngine.RHI.Vulkan;

namespace YamaEditor.Controls
{
    /// <summary>
    /// 引擎视口控件 —— 基于 Vulkan 渲染后端。
    /// 提供相机控制、视口模式切换、统计叠加层、网格与 Gizmo 控制。
    /// </summary>
    public sealed class EngineViewport : Control
    {
        // ───────── 常量 ─────────
        private const float CameraSpeed = 5.0f;
        private const float CameraBoostMultiplier = 3.0f;
        private const float MouseSensitivity = 0.002f;
        private const float ZoomMultiplier = 0.5f;

        // ───────── Vulkan 渲染 ─────────
        private VulkanDevice? _vulkanDevice;
        private VulkanSwapChain? _vulkanSwapChain;
        private VulkanCommandQueue? _vulkanQueue;
        private VulkanCommandList? _vulkanCmdList;
        private bool _vulkanInitialized;

        // ───────── 场景渲染器（网格 + 图元 + 三角形验证） ─────────
        private ViewportSceneRenderer? _sceneRenderer;

        // ───────── WinUI 面板引用 ─────────
        private SwapChainPanel? _swapChainPanel;
        private Border? _overlayRoot;
        private StackPanel? _statsOverlay;
        private TextBlock? _fpsText;
        private TextBlock? _trianglesText;
        private TextBlock? _drawCallsText;
        private TextBlock? _gpuMemoryText;
        private ComboBox? _viewportModeCombo;

        // ───────── 相机状态 ─────────
        private ViewportMode _currentMode = ViewportMode.Perspective;
        private bool _isOrbiting;
        private bool _isPanning;
        private float _lastPointerX;
        private float _lastPointerY;
        private readonly DispatcherTimer _statsTimer = new();
        private readonly HashSet<VirtualKey> _pressedKeys = new();

        // ───────── P/Invoke ─────────
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // ───────── 依赖属性 ─────────
        public static readonly DependencyProperty ShowGridProperty =
            DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(EngineViewport),
                new PropertyMetadata(true));

        public static readonly DependencyProperty ShowGizmosProperty =
            DependencyProperty.Register(nameof(ShowGizmos), typeof(bool), typeof(EngineViewport),
                new PropertyMetadata(true));

        public static readonly DependencyProperty ShowStatsProperty =
            DependencyProperty.Register(nameof(ShowStats), typeof(bool), typeof(EngineViewport),
                new PropertyMetadata(true));

        public static readonly DependencyProperty GizmoModeProperty =
            DependencyProperty.Register(nameof(GizmoMode), typeof(GizmoMode), typeof(EngineViewport),
                new PropertyMetadata(GizmoMode.Translate));

        public static readonly DependencyProperty ViewportModeProperty =
            DependencyProperty.Register(nameof(ViewportMode), typeof(ViewportMode), typeof(EngineViewport),
                new PropertyMetadata(ViewportMode.Perspective, OnViewportModeChanged));

        public bool ShowGrid
        {
            get => (bool)GetValue(ShowGridProperty);
            set => SetValue(ShowGridProperty, value);
        }

        public bool ShowGizmos
        {
            get => (bool)GetValue(ShowGizmosProperty);
            set => SetValue(ShowGizmosProperty, value);
        }

        public bool ShowStats
        {
            get => (bool)GetValue(ShowStatsProperty);
            set => SetValue(ShowStatsProperty, value);
        }

        public GizmoMode GizmoMode
        {
            get => (GizmoMode)GetValue(GizmoModeProperty);
            set => SetValue(GizmoModeProperty, value);
        }

        public ViewportMode ViewportMode
        {
            get => (ViewportMode)GetValue(ViewportModeProperty);
            set => SetValue(ViewportModeProperty, value);
        }

        // ───────── 事件 ─────────
        public event EventHandler<ViewportClickEventArgs>? ActorClicked;
        public event EventHandler? ViewportModeChanged;

        public EngineViewport()
        {
            DefaultStyleKey = typeof(EngineViewport);
            _statsTimer.Interval = TimeSpan.FromMilliseconds(250);
            _statsTimer.Tick += OnStatsTimerTick;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _swapChainPanel = GetTemplateChild("PART_SwapChainPanel") as SwapChainPanel;
            _overlayRoot = GetTemplateChild("PART_OverlayRoot") as Border;
            _statsOverlay = GetTemplateChild("PART_StatsOverlay") as StackPanel;
            _fpsText = GetTemplateChild("PART_FpsText") as TextBlock;
            _trianglesText = GetTemplateChild("PART_TrianglesText") as TextBlock;
            _drawCallsText = GetTemplateChild("PART_DrawCallsText") as TextBlock;
            _gpuMemoryText = GetTemplateChild("PART_GpuMemoryText") as TextBlock;
            _viewportModeCombo = GetTemplateChild("PART_ViewportModeCombo") as ComboBox;

            if (_viewportModeCombo != null)
            {
                _viewportModeCombo.ItemsSource = Enum.GetValues(typeof(ViewportMode));
                _viewportModeCombo.SelectedItem = ViewportMode;
                _viewportModeCombo.SelectionChanged += OnViewportModeComboChanged;
            }

            if (_swapChainPanel != null)
            {
                _swapChainPanel.SizeChanged += OnSwapChainPanelSizeChanged;
                _swapChainPanel.PointerPressed += OnSwapChainPanelPointerPressed;
                _swapChainPanel.PointerReleased += OnSwapChainPanelPointerReleased;
                _swapChainPanel.PointerMoved += OnSwapChainPanelPointerMoved;
                _swapChainPanel.PointerWheelChanged += OnSwapChainPanelPointerWheelChanged;
            }

            InitializeVulkan();
        }

        // ══════════════════════════════════════════════════════════
        //  Vulkan 初始化
        // ══════════════════════════════════════════════════════════
        private void InitializeVulkan()
        {
            if (_swapChainPanel == null || _vulkanInitialized) return;
            try
            {
                // 创建 Vulkan 设备（启用验证层用于调试）
                _vulkanDevice = new VulkanDevice(enableDebug: true);

                // 获取当前前台窗口 HWND 作为 Vulkan Surface 的宿主
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Debug.WriteLine("[EngineViewport] Failed to get HWND for Vulkan surface");
                    return;
                }

                var desc = new RHISwapChainDesc
                {
                    Width = (int)(_swapChainPanel.ActualWidth > 0 ? _swapChainPanel.ActualWidth : 800),
                    Height = (int)(_swapChainPanel.ActualHeight > 0 ? _swapChainPanel.ActualHeight : 600),
                    Format = RHIFormat.B8G8R8A8_UNorm,
                    BufferCount = 2,
                    Windowed = true,
                    VSync = true,
                    OutputWindow = hwnd,
                    SwapEffect = RHISwapEffect.FlipDiscard,
                    SampleCount = 1
                };

                _vulkanSwapChain = new VulkanSwapChain(_vulkanDevice, hwnd, desc);
                _vulkanQueue = (VulkanCommandQueue)_vulkanDevice.GetCommandQueue(RHIQueueType.Graphics);
                _vulkanCmdList = new VulkanCommandList(_vulkanDevice, RHIQueueType.Graphics, "EditorViewport");

                _vulkanInitialized = true;
                _statsTimer.Start();

                // 初始化场景渲染器（网格 + 线框图元 + 验证三角形）
                _sceneRenderer = new ViewportSceneRenderer(_vulkanDevice);
                int initW = (int)(_swapChainPanel.ActualWidth > 0 ? _swapChainPanel.ActualWidth : 800);
                int initH = (int)(_swapChainPanel.ActualHeight > 0 ? _swapChainPanel.ActualHeight : 600);
                _sceneRenderer.SetViewportSize(initW, initH);
                _sceneRenderer.Initialize();

                Debug.WriteLine("[EngineViewport] Vulkan initialized successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineViewport] Vulkan init failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  交换链尺寸同步
        // ══════════════════════════════════════════════════════════
        private void OnSwapChainPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_vulkanSwapChain == null || _vulkanDevice == null) return;
            try
            {
                var width = (int)Math.Max(1, e.NewSize.Width);
                var height = (int)Math.Max(1, e.NewSize.Height);
                _vulkanSwapChain.Resize(width, height);
                _sceneRenderer?.SetViewportSize(width, height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineViewport] Resize failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  相机输入处理
        // ══════════════════════════════════════════════════════════
        private void OnSwapChainPanelPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_swapChainPanel);
            _lastPointerX = point.Position.X;
            _lastPointerY = point.Position.Y;

            if (point.Properties.IsRightButtonPressed)
                _isOrbiting = true;
            else if (point.Properties.IsMiddleButtonPressed)
                _isPanning = true;
        }

        private void OnSwapChainPanelPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isOrbiting || _isPanning)
            {
                _isOrbiting = false;
                _isPanning = false;
            }
            else
            {
                // 单击选择 Actor
                var point = e.GetCurrentPoint(_swapChainPanel);
                var normalizedX = (float)(point.Position.X / (_swapChainPanel?.ActualWidth ?? 1));
                var normalizedY = (float)(point.Position.Y / (_swapChainPanel?.ActualHeight ?? 1));
                ActorClicked?.Invoke(this, new ViewportClickEventArgs(normalizedX, normalizedY));
            }
        }

        private void OnSwapChainPanelPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_swapChainPanel);
            float dx = point.Position.X - _lastPointerX;
            float dy = point.Position.Y - _lastPointerY;
            _lastPointerX = point.Position.X;
            _lastPointerY = point.Position.Y;

            var camera = _sceneRenderer?.Camera;
            if (camera == null) return;

            if (_isOrbiting)
            {
                camera.Orbit(dx, dy);
            }
            else if (_isPanning)
            {
                camera.Pan(-dx, -dy);
            }
        }

        private void OnSwapChainPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(_swapChainPanel).Properties.MouseWheelDelta;
            _sceneRenderer?.Camera.Zoom(delta);
        }

        // ══════════════════════════════════════════════════════════
        //  键盘相机移动（WASD + QE上下）
        // ══════════════════════════════════════════════════════════
        public void ProcessKeyboardInput(VirtualKey key, bool isDown)
        {
            if (isDown) _pressedKeys.Add(key);
            else _pressedKeys.Remove(key);
        }

        public void UpdateCameraMovement(float deltaTime)
        {
            var camera = _sceneRenderer?.Camera;
            if (camera == null) return;

            var move = Vector3.Zero;
            bool boost = _pressedKeys.Contains(VirtualKey.Shift);

            if (_pressedKeys.Contains(VirtualKey.W)) move.Z -= 1;
            if (_pressedKeys.Contains(VirtualKey.S)) move.Z += 1;
            if (_pressedKeys.Contains(VirtualKey.A)) move.X -= 1;
            if (_pressedKeys.Contains(VirtualKey.D)) move.X += 1;
            if (_pressedKeys.Contains(VirtualKey.E)) move.Y += 1;
            if (_pressedKeys.Contains(VirtualKey.Q)) move.Y -= 1;

            if (move.LengthSquared() > 0)
            {
                camera.Fly(move, deltaTime, boost);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  视口模式
        // ══════════════════════════════════════════════════════════
        private void OnViewportModeComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewportModeCombo?.SelectedItem is ViewportMode mode)
            {
                ViewportMode = mode;
                ApplyViewportMode(mode);
                ViewportModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private static void OnViewportModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EngineViewport vp && e.NewValue is ViewportMode mode)
                vp.ApplyViewportMode(mode);
        }

        private void ApplyViewportMode(ViewportMode mode)
        {
            var camera = _sceneRenderer?.Camera;
            if (camera == null) return;

            _currentMode = mode;

            switch (mode)
            {
                case ViewportMode.Perspective:
                    camera.IsOrtho = false;
                    break;
                case ViewportMode.Orthographic:
                    camera.IsOrtho = true;
                    break;
                case ViewportMode.Top:
                    camera.IsOrtho = true;
                    camera.LookFrom("Top");
                    break;
                case ViewportMode.Front:
                    camera.IsOrtho = true;
                    camera.LookFrom("Front");
                    break;
                case ViewportMode.Left:
                    camera.IsOrtho = true;
                    camera.LookFrom("Left");
                    break;
            }
        }

        public void SetViewportMode(ViewportMode mode)
        {
            ViewportMode = mode;
            if (_viewportModeCombo != null) _viewportModeCombo.SelectedItem = mode;
        }

        // ══════════════════════════════════════════════════════════
        //  统计叠加层更新
        // ══════════════════════════════════════════════════════════
        private void OnStatsTimerTick(object sender, object e)
        {
            if (!ShowStats || _statsOverlay == null) return;

            var systems = App.Systems;
            if (systems == null) return;

            _fpsText!.Text = $"FPS: {systems.Time.FPS:0.0}";
            _trianglesText!.Text = $"Tris: {systems.Performance.TriangleCount:N0}";
            _drawCallsText!.Text = $"Draw: {systems.Performance.DrawCalls}";
            _gpuMemoryText!.Text = $"GPU: {systems.Performance.AllocatedMemoryMB:N0} MB";
        }

        // ══════════════════════════════════════════════════════════
        //  渲染帧
        // ══════════════════════════════════════════════════════════
        public void RenderFrame()
        {
            if (!_vulkanInitialized || _vulkanSwapChain == null || _vulkanDevice == null || _vulkanQueue == null)
                return;

            try
            {
                // 获取下一帧图像
                _vulkanSwapChain.AcquireNextImage();

                if (_vulkanCmdList != null)
                {
                    _vulkanCmdList.Reset();
                    _vulkanCmdList.Open();

                    // 获取视口尺寸
                    int width = (int)(_swapChainPanel?.ActualWidth > 0 ? _swapChainPanel.ActualWidth : 800);
                    int height = (int)(_swapChainPanel?.ActualHeight > 0 ? _swapChainPanel.ActualHeight : 600);

                    // 同步场景渲染器的显示开关
                    if (_sceneRenderer != null)
                    {
                        _sceneRenderer.ShowGrid = ShowGrid;
                        _sceneRenderer.ShowGizmos = ShowGizmos;
                        _sceneRenderer.Update(1.0f / 60.0f);
                    }

                    // 渲染整个场景（网格 + 图元 + 验证三角形）
                    var rtv = _vulkanSwapChain.GetCurrentBackBufferRTV();
                    _sceneRenderer?.Render(_vulkanCmdList, rtv);

                    // 结束渲染
                    _vulkanCmdList.Close();
                }

                // 提交命令并呈现
                _vulkanSwapChain.Present(_vulkanQueue, _vulkanCmdList);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineViewport] RenderFrame failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  释放资源
        // ══════════════════════════════════════════════════════════
        public void Cleanup()
        {
            _statsTimer.Stop();
            _sceneRenderer?.Dispose();
            _vulkanCmdList?.Dispose();
            _vulkanSwapChain?.Dispose();
            _vulkanDevice?.Dispose();
            _vulkanInitialized = false;
        }
    }

    /// <summary>
    /// 视口模式枚举
    /// </summary>
    public enum ViewportMode
    {
        Perspective,
        Orthographic,
        Top,
        Front,
        Left
    }

    /// <summary>
    /// 视口点击事件参数
    /// </summary>
    public sealed class ViewportClickEventArgs : EventArgs
    {
        public float NormalizedX { get; }
        public float NormalizedY { get; }
        public bool Handled { get; set; }

        public ViewportClickEventArgs(float x, float y)
        {
            NormalizedX = x;
            NormalizedY = y;
        }
    }
}
