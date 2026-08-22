using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Input;

namespace ShanyuEngine.UI
{
    // ──────────────────────────────────────────────────────────────
    // HitTestResult - 命中测试结果
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 命中测试结果：包含命中的 Widget、本地坐标和距离。
    /// </summary>
    public struct HitTestResult
    {
        /// <summary>命中的 Widget（最上层可交互的）。</summary>
        public WidgetBase Widget;
        /// <summary>在 Widget 局部空间中的坐标。</summary>
        public Vector2 LocalPosition;
        /// <summary>命中点到 Widget 中心的距离（用于排序优先）。</summary>
        public float Distance;

        public bool IsValid => Widget != null;

        public static readonly HitTestResult Invalid = default;

        public HitTestResult(WidgetBase widget, Vector2 localPos, float distance = 0)
        {
            Widget = widget;
            LocalPosition = localPos;
            Distance = distance;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // WidgetTree - Widget 树
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Widget 树 — 管理根 Widget 列表，维护绘制顺序和布局更新标记。
    /// </summary>
    public class WidgetTree
    {
        /// <summary>根 Widget 列表（多个 Canvas）。</summary>
        private readonly List<WidgetBase> _roots = new();
        public IReadOnlyList<WidgetBase> Roots => _roots;

        /// <summary>布局是否需要重新计算。</summary>
        public bool IsLayoutDirty { get; private set; } = true;

        /// <summary>添加根 Widget。</summary>
        public void AddRoot(WidgetBase widget)
        {
            if (widget == null) throw new ArgumentNullException(nameof(widget));
            if (widget.Parent != null) widget.RemoveFromParent();
            if (!_roots.Contains(widget))
            {
                _roots.Add(widget);
                MarkLayoutDirty();
            }
        }

        /// <summary>移除根 Widget。</summary>
        public bool RemoveRoot(WidgetBase widget)
        {
            if (_roots.Remove(widget))
            {
                MarkLayoutDirty();
                return true;
            }
            return false;
        }

        /// <summary>清空所有根 Widget。</summary>
        public void Clear()
        {
            foreach (var root in _roots)
                root.Destroy();
            _roots.Clear();
            MarkLayoutDirty();
        }

        /// <summary>标记布局为脏。</summary>
        public void MarkLayoutDirty()
        {
            IsLayoutDirty = true;
        }

        /// <summary>按 ZOrder 排序后的所有根 Widget。</summary>
        public List<WidgetBase> GetSortedRoots()
        {
            var sorted = new List<WidgetBase>(_roots);
            sorted.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
            return sorted;
        }

        /// <summary>获取树中所有 Widget 的总数。</summary>
        public int GetTotalWidgetCount()
        {
            int count = 0;
            foreach (var root in _roots)
                count += CountWidgets(root);
            return count;
        }

        private static int CountWidgets(WidgetBase widget)
        {
            int count = 1;
            foreach (var child in widget.Children)
                count += CountWidgets(child);
            return count;
        }

        /// <summary>在树中按名称查找 Widget。</summary>
        public WidgetBase FindWidgetByName(string name)
        {
            foreach (var root in _roots)
            {
                var found = root.FindChild(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>在树中按类型查找所有 Widget。</summary>
        public List<T> FindWidgetsByType<T>() where T : WidgetBase
        {
            var result = new List<T>();
            foreach (var root in _roots)
                CollectByType(root, result);
            return result;
        }

        private static void CollectByType<T>(WidgetBase widget, List<T> list) where T : WidgetBase
        {
            if (widget is T t) list.Add(t);
            foreach (var child in widget.Children)
                CollectByType(child, list);
        }

        /// <summary>执行布局更新（递归）。</summary>
        public void UpdateLayout()
        {
            foreach (var root in _roots)
            {
                if (root.LayoutDirty)
                {
                    root.Arrange(root.CachedRect);
                    root.ArrangeChildren();
                }
            }
            IsLayoutDirty = false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // WidgetSystem - UI 系统入口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Widget 系统 — UI 系统的顶层入口。
    /// 管理多个 Canvas、焦点、命中测试、输入事件分发和全局 Update/Render。
    /// 相当于 UE UMG 的 UGameViewportClient 中的 UI 子系统 / Unity CanvasScaler。
    /// </summary>
    public sealed class WidgetSystem
    {
        // ── 单例 ──────────────────────────────────────────────────
        private static WidgetSystem _instance;
        public static WidgetSystem Instance =>
            _instance ?? throw new InvalidOperationException("WidgetSystem has not been initialized.");

        // ── 组件 ──────────────────────────────────────────────────
        public WidgetTree Tree { get; private set; }
        public CanvasRenderer Renderer { get; private set; }

        // ── 焦点管理 ──────────────────────────────────────────────
        /// <summary>当前拥有键盘焦点的 Widget。</summary>
        public WidgetBase FocusedWidget { get; private set; }

        /// <summary>当前鼠标悬停的 Widget。</summary>
        public WidgetBase HoveredWidget { get; private set; }

        // ── 输入状态 ──────────────────────────────────────────────
        private WidgetBase _pressedWidget;
        private WidgetBase _draggedWidget;
        private Vector2 _dragStartPosition;
        private Vector2 _lastMousePosition;
        private Vector2 _mousePosition;
        private bool _mouseInViewport;

        // ── Tab 导航 ──────────────────────────────────────────────
        private readonly List<WidgetBase> _focusableWidgets = new();

        // ── 视口 ──────────────────────────────────────────────────
        public float ViewportWidth { get; private set; }
        public float ViewportHeight { get; private set; }
        public Vector2 ViewportSize => new Vector2(ViewportWidth, ViewportHeight);

        // ── 事件 ──────────────────────────────────────────────────
        /// <summary>全局输入事件（可用于 UI 音效/分析）。</summary>
        public event Action<WidgetBase, MouseButton> OnAnyClick;
        public event Action<WidgetBase> OnFocusChanged;

        // ── 构造 ──────────────────────────────────────────────────
        public WidgetSystem(CanvasRenderer renderer)
        {
            _instance = this;
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            Tree = new WidgetTree();
        }

        /// <summary>初始化 Widget 系统。</summary>
        public void Initialize(float viewportWidth, float viewportHeight)
        {
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
        }

        /// <summary>重置单例（用于测试）。</summary>
        internal static void ResetInstance() => _instance = null;

        // ── Canvas 管理 ───────────────────────────────────────────

        /// <summary>创建并添加一个新的 CanvasPanel 作为根 Canvas。</summary>
        public CanvasPanel CreateCanvas(string name = "Canvas")
        {
            var canvas = new CanvasPanel { Name = name };
            canvas.Arrange(new Rectangle(0, 0, ViewportWidth, ViewportHeight));
            Tree.AddRoot(canvas);
            return canvas;
        }

        /// <summary>移除一个 Canvas。</summary>
        public void RemoveCanvas(CanvasPanel canvas)
        {
            Tree.RemoveRoot(canvas);
        }

        /// <summary>获取所有 Canvas。</summary>
        public List<CanvasPanel> GetCanvases()
        {
            var canvases = new List<CanvasPanel>();
            foreach (var root in Tree.Roots)
            {
                if (root is CanvasPanel cp)
                    canvases.Add(cp);
            }
            return canvases;
        }

        // ── Widget 添加/移除 ──────────────────────────────────────

        /// <summary>添加 Widget 到视口（添加到第一个 Canvas 或创建默认 Canvas）。</summary>
        public WidgetBase AddToViewport(WidgetBase widget)
        {
            var canvases = GetCanvases();
            CanvasPanel target;
            if (canvases.Count > 0)
                target = canvases[0];
            else
                target = CreateCanvas("DefaultCanvas");

            target.AddChild(widget);
            return widget;
        }

        /// <summary>从视口移除 Widget。</summary>
        public bool RemoveFromViewport(WidgetBase widget)
        {
            if (widget.Parent != null)
            {
                widget.RemoveFromParent();
                return true;
            }
            return false;
        }

        // ── 焦点管理 ──────────────────────────────────────────────

        /// <summary>设置键盘焦点到指定 Widget。</summary>
        public void SetFocus(WidgetBase widget)
        {
            if (widget == FocusedWidget) return;

            FocusedWidget?.OnFocusLost();
            FocusedWidget = widget;
            widget?.OnFocusGained();
            OnFocusChanged?.Invoke(widget);
        }

        /// <summary>清除键盘焦点。</summary>
        public void ClearFocus()
        {
            if (FocusedWidget != null)
            {
                FocusedWidget.OnFocusLost();
                FocusedWidget = null;
                OnFocusChanged?.Invoke(null);
            }
        }

        /// <summary>Tab 导航到下一个可聚焦 Widget。</summary>
        public void FocusNextWidget()
        {
            BuildFocusableList();
            if (_focusableWidgets.Count == 0) return;

            int currentIndex = _focusableWidgets.IndexOf(FocusedWidget);
            int nextIndex = (currentIndex + 1) % _focusableWidgets.Count;
            SetFocus(_focusableWidgets[nextIndex]);
        }

        /// <summary>Tab 导航到上一个可聚焦 Widget。</summary>
        public void FocusPreviousWidget()
        {
            BuildFocusableList();
            if (_focusableWidgets.Count == 0) return;

            int currentIndex = _focusableWidgets.IndexOf(FocusedWidget);
            int prevIndex = (currentIndex - 1 + _focusableWidgets.Count) % _focusableWidgets.Count;
            SetFocus(_focusableWidgets[prevIndex]);
        }

        private void BuildFocusableList()
        {
            _focusableWidgets.Clear();
            foreach (var root in Tree.Roots)
                CollectFocusable(root);
        }

        private void CollectFocusable(WidgetBase widget)
        {
            // 可聚焦的 Widget 条件：可见、启用、且是可聚焦类型（如 EditableTextBox, Button）
            if (widget.IsHitTestVisible && widget.IsEnabled)
            {
                if (widget is EditableTextBox || widget is Button || widget is ComboBox)
                    _focusableWidgets.Add(widget);
            }
            foreach (var child in widget.Children)
                CollectFocusable(child);
        }

        // ── 命中测试 ──────────────────────────────────────────────

        /// <summary>
        /// 从指定根 Widget 开始执行命中测试。
        /// 返回最上层可交互的 Widget。
        /// </summary>
        public HitTestResult HitTest(Vector2 screenPosition, WidgetBase root = null)
        {
            var roots = root != null ? new List<WidgetBase> { root } : Tree.GetSortedRoots();

            // 从后往前（先绘制的最上层）
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                var result = HitTestWidget(roots[i], screenPosition);
                if (result.IsValid)
                    return result;
            }

            return HitTestResult.Invalid;
        }

        /// <summary>递归命中测试单个 Widget。</summary>
        private static HitTestResult HitTestWidget(WidgetBase widget, Vector2 screenPos)
        {
            if (!widget.IsHitTestVisible) return HitTestResult.Invalid;
            if (widget.Opacity <= 0f) return HitTestResult.Invalid;

            // 获取屏幕空间矩形
            var screenRect = widget.GetScreenRect();
            if (!screenRect.Contains(screenPos))
                return HitTestResult.Invalid;

            // 子 Widget 优先（分层命中测试）
            // 按 ZOrder 从高到低测试
            for (int i = widget.Children.Count - 1; i >= 0; i--)
            {
                var childResult = HitTestWidget(widget.Children[i], screenPos);
                if (childResult.IsValid)
                    return childResult;
            }

            // 自己可命中
            var localPos = widget.AbsoluteToLocal(screenPos);
            float dist = Vector2.Distance(localPos, widget.Size * 0.5f);
            return new HitTestResult(widget, localPos, dist);
        }

        // ── 坐标转换 ──────────────────────────────────────────────

        /// <summary>将屏幕坐标转换为指定 Widget 的本地坐标。</summary>
        public Vector2 ScreenToWidgetLocal(WidgetBase widget, Vector2 screenPos)
        {
            return widget.AbsoluteToLocal(screenPos);
        }

        /// <summary>将 Widget 本地坐标转换为屏幕坐标。</summary>
        public Vector2 WidgetLocalToScreen(WidgetBase widget, Vector2 localPos)
        {
            return widget.LocalToAbsolute(localPos);
        }

        // ── 输入事件分发 ──────────────────────────────────────────

        /// <summary>分发鼠标按下事件。</summary>
        public void DispatchMouseDown(MouseButton button, Vector2 screenPosition)
        {
            _mousePosition = screenPosition;

            var result = HitTest(screenPosition);
            if (!result.IsValid) return;

            _pressedWidget = result.Widget;
            result.Widget.OnMouseDown(result.LocalPosition, button);

            // 焦点切换：可交互控件获得焦点
            if (result.Widget is EditableTextBox || result.Widget is Button ||
                result.Widget is Slider || result.Widget is ComboBox)
            {
                SetFocus(result.Widget);
            }
        }

        /// <summary>分发鼠标抬起事件。</summary>
        public void DispatchMouseUp(MouseButton button, Vector2 screenPosition)
        {
            _mousePosition = screenPosition;

            if (_pressedWidget != null)
            {
                var localPos = _pressedWidget.AbsoluteToLocal(screenPosition);
                _pressedWidget.OnMouseUp(localPos, button);

                // 检测点击
                var hitResult = HitTest(screenPosition);
                if (hitResult.Widget == _pressedWidget)
                {
                    _pressedWidget.OnClick(localPos, button);
                    OnAnyClick?.Invoke(_pressedWidget, button);
                }

                // 结束拖拽
                if (_draggedWidget != null)
                {
                    _draggedWidget.OnDragEnd(localPos);
                    _draggedWidget = null;
                }
            }

            _pressedWidget = null;
        }

        /// <summary>分发鼠标移动事件。</summary>
        public void DispatchMouseMove(Vector2 screenPosition)
        {
            Vector2 delta = screenPosition - _lastMousePosition;
            _mousePosition = screenPosition;
            _mouseInViewport = true;

            // 悬停检测
            var hitResult = HitTest(screenPosition);
            var hitWidget = hitResult.IsValid ? hitResult.Widget : null;

            if (hitWidget != HoveredWidget)
            {
                HoveredWidget?.OnMouseLeave();
                HoveredWidget = hitWidget;
                hitWidget?.OnMouseEnter();
            }

            // 鼠标移动事件
            if (hitWidget != null)
            {
                hitWidget.OnMouseMove(hitResult.LocalPosition);
            }

            // 拖拽
            if (_pressedWidget != null && _draggedWidget == null)
            {
                float dragDist = Vector2.Distance(screenPosition, _lastMousePosition);
                if (dragDist > 4f) // 4px 拖拽阈值
                {
                    _draggedWidget = _pressedWidget;
                    _dragStartPosition = _pressedWidget.AbsoluteToLocal(_lastMousePosition);
                    _draggedWidget.OnDragStart(_dragStartPosition);
                }
            }

            if (_draggedWidget != null)
            {
                var dragLocal = _draggedWidget.AbsoluteToLocal(screenPosition);
                _draggedWidget.OnDragMove(dragLocal);
            }

            _lastMousePosition = screenPosition;
        }

        /// <summary>分发鼠标滚轮事件。</summary>
        public void DispatchMouseWheel(Vector2 screenPosition, float delta)
        {
            _mousePosition = screenPosition;

            var result = HitTest(screenPosition);
            if (result.IsValid)
            {
                result.Widget.OnScroll(delta);
            }
        }

        /// <summary>分发键盘按下事件。</summary>
        public void DispatchKeyDown(InputKey key)
        {
            // Tab 导航
            if (key == InputKey.Tab)
            {
                // 检测 Shift 是否按下（简化：无 Shift 修饰符检测）
                FocusNextWidget();
                return;
            }

            FocusedWidget?.OnKeyDown(key);
        }

        /// <summary>分发键盘抬起事件。</summary>
        public void DispatchKeyUp(InputKey key)
        {
            FocusedWidget?.OnKeyUp(key);
        }

        /// <summary>分发字符输入事件。</summary>
        public void DispatchTextInput(char c)
        {
            FocusedWidget?.OnTextInput(c);
        }

        /// <summary>分发触摸事件（转换为鼠标事件）。</summary>
        public void DispatchTouchDown(int touchId, Vector2 position)
        {
            DispatchMouseDown(MouseButton.Left, position);
        }

        public void DispatchTouchUp(int touchId, Vector2 position)
        {
            DispatchMouseUp(MouseButton.Left, position);
        }

        public void DispatchTouchMove(int touchId, Vector2 position)
        {
            DispatchMouseMove(position);
        }

        /// <summary>设置鼠标位置（由外部输入系统调用）。</summary>
        public void SetMousePosition(Vector2 position)
        {
            _mousePosition = position;
            _lastMousePosition = position;
        }

        // ── 全局更新 ──────────────────────────────────────────────

        /// <summary>
        /// 每帧调用：更新布局、Widget 逻辑、动画。
        /// </summary>
        public void Update(float deltaTime)
        {
            // 更新布局
            if (Tree.IsLayoutDirty)
                Tree.UpdateLayout();

            // Tick 所有 Widget
            foreach (var root in Tree.Roots)
            {
                if (root.LayoutDirty)
                {
                    root.Arrange(root.CachedRect);
                    root.ArrangeChildren();
                }
                root.Tick(deltaTime);
            }
        }

        // ── 全局渲染 ──────────────────────────────────────────────

        /// <summary>
        /// 每帧调用：渲染所有 Widget。
        /// </summary>
        public void Render()
        {
            if (Renderer == null) return;

            Renderer.BeginFrame();

            var sortedRoots = Tree.GetSortedRoots();
            var paintContext = new PaintContext(Renderer);

            foreach (var root in sortedRoots)
            {
                if (root.Visibility == EVisibility.Collapsed) continue;
                if (root.Opacity <= 0f) continue;

                var rect = root.CachedRect;
                paintContext.PushOpacity(root.Opacity);

                if (root is CanvasPanel canvas)
                {
                    // CanvasPanel 处理子节点渲染
                    canvas.OnPaint(paintContext, rect);
                }
                else
                {
                    // 普通根 Widget
                    root.OnPaint(paintContext, rect);
                    // 递归绘制子节点
                    RenderWidgetTree(root, paintContext);
                }

                paintContext.PopOpacity();
            }

            Renderer.EndFrame();
        }

        /// <summary>递归渲染 Widget 树。</summary>
        private static void RenderWidgetTree(WidgetBase widget, PaintContext context)
        {
            // 按 ZOrder 排序绘制子节点
            var sortedChildren = new List<WidgetBase>(widget.Children);
            sortedChildren.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));

            foreach (var child in sortedChildren)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                if (child.Opacity <= 0f) continue;

                context.PushOpacity(child.Opacity);

                if (child.Clipping == ClippingMode.ClipToBounds)
                    context.PushClip(child.GetScreenRect());

                child.OnPaint(context, child.CachedRect);

                if (child.Children.Count > 0)
                    RenderWidgetTree(child, context);

                if (child.Clipping == ClippingMode.ClipToBounds)
                    context.PopClip();

                context.PopOpacity();
            }
        }

        // ── UI 缩放 ──────────────────────────────────────────────

        /// <summary>设置视口尺寸（窗口大小变化时调用）。</summary>
        public void SetViewportSize(float width, float height)
        {
            ViewportWidth = width;
            ViewportHeight = height;

            // 重新布局根 Canvas
            foreach (var root in Tree.Roots)
            {
                if (root is CanvasPanel)
                {
                    root.Arrange(new Rectangle(0, 0, width, height));
                    root.ArrangeChildren();
                }
            }
        }

        // ── 统计 ──────────────────────────────────────────────────

        /// <summary>获取系统统计信息。</summary>
        public string GetStats()
        {
            return $"Widgets: {Tree.GetTotalWidgetCount()} | Roots: {Tree.Roots.Count} | " +
                   $"Focus: {(FocusedWidget?.Name ?? "none")} | Hover: {(HoveredWidget?.Name ?? "none")}";
        }
    }
}
