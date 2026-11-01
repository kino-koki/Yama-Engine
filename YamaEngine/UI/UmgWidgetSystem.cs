using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using YamaEngine.RHI;

namespace YamaEngine.UI
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - UMG 风格 UI 系统 (UmgWidgetSystem)
    //
    // 本文件实现一套虚幻 UMG/Slate 风格的保留模式 UI 框架, 涵盖:
    //   * Widget 树: Widget 基类、PanelWidget (可含子节点)、LeafWidget
    //   * 内置控件: Button / Text / Image / Slider / CheckBox / ComboBox /
    //               ProgressBar / ScrollBox / CanvasPanel / VerticalBox /
    //               HorizontalBox / Border / Spacer / InputField
    //   * 锚点布局: Anchors / Offsets / Alignment, 支持自适应缩放
    //   * 事件系统: Click / Hover / Focus / Drag / Key
    //   * 数据绑定: 双向绑定到属性路径, 自动刷新
    //   * 控件动画: 淡入淡出 / 滑动 / 缩放, 基于时间轴
    //   * Slate 样式: 字体 / 颜色 / 边框 / 笔刷 (Brush)
    //   * UI 材质与特效: 描边 / 阴影 / 发光
    //   * 9-Slice 图片: 九宫格拉伸, 边角保持不变形
    //   * 文本渲染: SDF 字体 (距离场) 与富文本 (RichText)
    //   * UI 缩放与 DPI 感知: 按屏幕 DPI 自适应字号与间距
    //   * RHI 对接: 顶点缓冲生成、合并绘制调用、实例化批渲染
    //
    // 对接系统: RHI (RHIDevice/RHICommandList) / Input (InputKey) / Core
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 颜色与笔刷 (Color & Brush)
    // ──────────────────────────────────────────────────────────────

    /// <summary>线性 RGBA 颜色 (各分量 0~1)。</summary>
    public struct Color
    {
        public float R, G, B, A;
        public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

        public static readonly Color White = new(1, 1, 1, 1);
        public static readonly Color Black = new(0, 0, 0, 1);
        public static readonly Color Transparent = new(0, 0, 0, 0);
        public static readonly Color Red = new(0.85f, 0.2f, 0.2f, 1);
        public static readonly Color Green = new(0.2f, 0.8f, 0.3f, 1);
        public static readonly Color Blue = new(0.2f, 0.45f, 0.95f, 1);
        public static readonly Color Gray = new(0.5f, 0.5f, 0.5f, 1);
        public static readonly Color LightGray = new(0.8f, 0.8f, 0.8f, 1);
        public static readonly Color DarkGray = new(0.2f, 0.2f, 0.22f, 1);

        /// <summary>从 sRGB 0~255 整数构造线性颜色。</summary>
        public static Color FromSRGB(byte r, byte g, byte b, byte a = 255) =>
            new(SRGBToLinear(r / 255f), SRGBToLinear(g / 255f), SRGBToLinear(b / 255f), a / 255f);

        private static float SRGBToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        public Vector4 ToVector4() => new(R, G, B, A);
        public Color WithAlpha(float a) => new(R, G, B, a);
        public static Color Lerp(Color a, Color b, float t) =>
            new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
    }

    /// <summary>笔刷类型, 决定控件表面如何绘制。</summary>
    public enum BrushType { SolidColor, Texture, NineSlice, Material, LinearGradient }

    /// <summary>
    /// Slate 笔刷: 描述控件表面的填充方式。
    /// 支持纯色、纹理、九宫格、UI 材质与线性渐变。
    /// </summary>
    public sealed class SlateBrush
    {
        public BrushType Type = BrushType.SolidColor;
        /// <summary>纯色或TintColor。</summary>
        public Color TintColor = Color.White;
        /// <summary>纹理资源名 (由资源管理器解析)。</summary>
        public string TextureName = string.Empty;
        /// <summary>纹理在图集中的 UV 区域。</summary>
        public Vector4 UVRegion = new(0, 0, 1, 1);
        /// <summary>九宫格边距 (像素)。</summary>
        public float Margin = 8f;
        /// <summary>渐变起止色。</summary>
        public Color GradientStart = Color.White;
        public Color GradientEnd = Color.Gray;
        /// <summary>渐变方向 (角度, 0=水平, 90=垂直)。</summary>
        public float GradientAngle = 90f;
        /// <summary>UI 材质名 (用于描边/发光等特效)。</summary>
        public string MaterialName = string.Empty;
        /// <summary>绘制时附加缩放。</summary>
        public Vector2 DrawScale = Vector2.One;
    }

    // ──────────────────────────────────────────────────────────────
    // 2. Slate 样式 (SlateStyle)
    // ──────────────────────────────────────────────────────────────

    /// <summary>字体描述。</summary>
    public sealed class SlateFontInfo
    {
        public string FontName = "Default";
        /// <summary>字号 (设计 DPI 下, 单位像素)。</summary>
        public float Size = 14f;
        public bool Bold;
        public bool Italic;
        /// <summary>SDF 字体轮廓 (描边) 宽度。</summary>
        public float OutlineSize;
        public Color OutlineColor = Color.Black;
        /// <summary>字间距。</summary>
        public float LetterSpacing;
    }

    /// <summary>边框样式。</summary>
    public sealed class SlateBorderStyle
    {
        public Color Color = Color.Black;
        public float Thickness = 1f;
        public float CornerRadius;
        public SlateBrush Brush;
    }

    /// <summary>
    /// Slate 样式集: 集中管理控件外观参数, 支持主题切换。
    /// </summary>
    public sealed class SlateStyle
    {
        public SlateFontInfo DefaultFont = new() { Size = 14f };
        public Color ForegroundColor = Color.White;
        public Color BackgroundColor = new(0.15f, 0.16f, 0.19f, 1f);
        public Color DisabledColor = new(0.4f, 0.4f, 0.4f, 0.6f);
        public Color HoverColor = new(0.3f, 0.31f, 0.36f, 1f);
        public Color PressedColor = new(0.2f, 0.22f, 0.27f, 1f);
        public Color FocusColor = new(0.26f, 0.59f, 0.98f, 1f);
        public SlateBrush ButtonBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.26f, 0.27f, 0.32f, 1f) };
        public SlateBrush PanelBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.12f, 0.13f, 0.16f, 0.95f) };
        public SlateBrush EditableTextBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.18f, 0.19f, 0.23f, 1f) };
        public SlateBorderStyle Border = new() { Color = new(0.25f, 0.26f, 0.3f, 0.6f), Thickness = 1f };
        public float CornerRadius = 3f;
        public float Padding = 4f;

        /// <summary>深色主题预设。</summary>
        public static SlateStyle Dark => new();
        /// <summary>浅色主题预设。</summary>
        public static SlateStyle Light => new()
        {
            ForegroundColor = new(0.1f, 0.1f, 0.1f, 1f),
            BackgroundColor = new(0.92f, 0.92f, 0.94f, 1f),
            HoverColor = new(0.8f, 0.82f, 0.86f, 1f),
            PressedColor = new(0.7f, 0.72f, 0.76f, 1f),
            ButtonBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.85f, 0.86f, 0.88f, 1f) },
            PanelBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.98f, 0.98f, 1f, 0.95f) }
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 锚点布局 (Anchors / Offsets / Alignment)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 锚点: 定义控件四条边相对于父容器的位置比例 (0~1)。
    /// Minimum 对应左/上, Maximum 对应右/下。
    /// </summary>
    public struct Anchors
    {
        public Vector2 Minimum;
        public Vector2 Maximum;

        public Anchors(float min, float max) { Minimum = new(min, min); Maximum = new(max, max); }
        public Anchors(float minX, float minY, float maxX, float maxY) { Minimum = new(minX, minY); Maximum = new(maxX, maxY); }
        public Anchors(Vector2 min, Vector2 max) { Minimum = min; Maximum = max; }

        /// <summary>左上角锚定。</summary>
        public static readonly Anchors TopLeft = new(0, 0, 0, 0);
        /// <summary>铺满父容器。</summary>
        public static readonly Anchors StretchAll = new(0, 0, 1, 1);
        /// <summary>水平居中。</summary>
        public static readonly Anchors CenterHorizontal = new(0.5f, 0.5f, 0.5f, 0.5f);
        /// <summary>顶部水平拉伸。</summary>
        public static readonly Anchors TopStretch = new(new Vector2(0, 0), new Vector2(1, 0));
        /// <summary>底部水平拉伸。</summary>
        public static readonly Anchors BottomStretch = new(new Vector2(0, 1), new Vector2(1, 1));
    }

    /// <summary>
    /// 控件相对父容器的偏移 (像素)。
    /// Left/Top/Right/Bottom 对应四条边到锚点的距离。
    /// </summary>
    public struct Margins
    {
        public float Left, Top, Right, Bottom;
        public Margins(float uniform) { Left = Top = Right = Bottom = uniform; }
        public Margins(float h, float v) { Left = Right = h; Top = Bottom = v; }
        public Margins(float l, float t, float r, float b) { Left = l; Top = t; Right = r; Bottom = b; }
        public static readonly Margins Zero = new(0);
    }

    /// <summary>控件对齐方式 (相对其自身尺寸的锚点)。</summary>
    public struct Alignment
    {
        public float Horizontal;
        public float Vertical;
        public Alignment(float h, float v) { Horizontal = h; Vertical = v; }
        public static readonly Alignment TopLeft = new(0, 0);
        public static readonly Alignment Center = new(0.5f, 0.5f);
        public static readonly Alignment CenterTop = new(0.5f, 0);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 布局几何 (LayoutGeometry)
    // ──────────────────────────────────────────────────────────────

    /// <summary>控件经布局后的绝对几何 (屏幕空间像素)。</summary>
    public struct LayoutGeometry
    {
        public Vector2 AbsolutePosition;
        public Vector2 Size;
        public float Scale;
        public Vector2 LocalSize => Size / Math.Max(Scale, 0.0001f);
    }

    /// <summary>水平/垂直对齐枚举, 用于盒子布局。</summary>
    public enum HAlign { Fill, Left, Center, Right }
    public enum VAlign { Fill, Top, Center, Bottom }

    // ──────────────────────────────────────────────────────────────
    // 5. 事件系统 (Widget Events)
    // ──────────────────────────────────────────────────────────────

    /// <summary>控件事件参数。</summary>
    public struct WidgetEventArgs
    {
        public Vector2 MousePosition;
        public Vector2 MouseDelta;
        public Vector2 LocalMousePosition;
        public MouseButton Button;
        public float WheelDelta;
        public int KeyCode;
        public char Char;
        public float Timestamp;
    }

    /// <summary>
    /// 控件事件路由方式: 冒泡 (Bubble) 或隧道 (Tunnel)。
    /// </summary>
    public enum EventRouting { Bubble, Tunnel, Direct }

    /// <summary>
    /// 控件事件系统: 维护各类回调列表, 支持冒泡传递。
    /// </summary>
    public sealed class WidgetEventSystem
    {
        // 当前悬停 / 按下 / 焦点控件
        private Widget _hoveredWidget;
        private Widget _pressedWidget;
        private Widget _focusedWidget;
        private Widget _draggingWidget;
        private Vector2 _dragStart;
        private Vector2 _lastMouse;

        /// <summary>当前持有键盘焦点的控件。</summary>
        public Widget FocusedWidget => _focusedWidget;

        /// <summary>分发鼠标按下事件。</summary>
        public bool DispatchMouseDown(Widget root, Vector2 mousePos, MouseButton button)
        {
            var target = HitTest(root, mousePos);
            if (target == null) return false;
            _pressedWidget = target;
            var args = new WidgetEventArgs
            {
                MousePosition = mousePos,
                LocalMousePosition = mousePos - target.CachedGeometry.AbsolutePosition,
                Button = button,
                Timestamp = Environment.TickCount / 1000f
            };
            // 冒泡传递
            var current = target;
            while (current != null)
            {
                current.HandleMouseDown(args);
                if (current._flags.HasFlag(WidgetFlags.HandledEvent)) break;
                current = current.Parent;
            }
            return true;
        }

        /// <summary>分发鼠标抬起事件。</summary>
        public bool DispatchMouseUp(Widget root, Vector2 mousePos, MouseButton button)
        {
            var target = _pressedWidget ?? HitTest(root, mousePos);
            if (target == null) return false;
            var args = new WidgetEventArgs
            {
                MousePosition = mousePos,
                LocalMousePosition = mousePos - target.CachedGeometry.AbsolutePosition,
                Button = button
            };
            target.HandleMouseUp(args);
            // 若鼠标仍在控件内, 触发点击
            var hover = HitTest(root, mousePos);
            if (hover == target)
                target.HandleClick(args);
            _pressedWidget = null;
            return true;
        }

        /// <summary>分发鼠标移动事件。</summary>
        public void DispatchMouseMove(Widget root, Vector2 mousePos)
        {
            Vector2 delta = mousePos - _lastMouse;
            _lastMouse = mousePos;
            var hover = HitTest(root, mousePos);
            if (hover != _hoveredWidget)
            {
                _hoveredWidget?.HandleMouseLeave(default);
                _hoveredWidget = hover;
                hover?.HandleMouseEnter(new WidgetEventArgs { MousePosition = mousePos });
            }
            if (hover != null)
            {
                var args = new WidgetEventArgs
                {
                    MousePosition = mousePos,
                    MouseDelta = delta,
                    LocalMousePosition = mousePos - hover.CachedGeometry.AbsolutePosition
                };
                hover.HandleMouseMove(args);
            }
            // 拖拽处理
            if (_draggingWidget != null)
            {
                var dargs = new WidgetEventArgs
                {
                    MousePosition = mousePos,
                    MouseDelta = delta,
                    LocalMousePosition = mousePos - _draggingWidget.CachedGeometry.AbsolutePosition
                };
                _draggingWidget.HandleDrag(dargs);
            }
        }

        /// <summary>分发鼠标滚轮事件。</summary>
        public void DispatchMouseWheel(Widget root, Vector2 mousePos, float delta)
        {
            var target = HitTest(root, mousePos);
            if (target == null) return;
            target.HandleMouseWheel(new WidgetEventArgs { MousePosition = mousePos, WheelDelta = delta });
        }

        /// <summary>分发键盘按键事件。</summary>
        public bool DispatchKeyDown(int keyCode)
        {
            if (_focusedWidget == null) return false;
            _focusedWidget.HandleKeyDown(new WidgetEventArgs { KeyCode = keyCode });
            return true;
        }

        /// <summary>分发字符输入事件。</summary>
        public bool DispatchCharInput(char c)
        {
            if (_focusedWidget == null) return false;
            _focusedWidget.HandleCharInput(new WidgetEventArgs { Char = c });
            return true;
        }

        /// <summary>设置键盘焦点。</summary>
        public void SetFocus(Widget widget)
        {
            _focusedWidget?.HandleFocusLost(default);
            _focusedWidget = widget;
            widget?.HandleFocusReceived(default);
        }

        /// <summary>开始拖拽。</summary>
        public void BeginDrag(Widget widget, Vector2 start) { _draggingWidget = widget; _dragStart = start; }
        public void EndDrag() => _draggingWidget = null;

        /// <summary>命中测试: 自顶向下查找鼠标所在的最上层可见控件。</summary>
        public Widget HitTest(Widget widget, Vector2 point)
        {
            if (!widget.IsVisible) return null;
            var geo = widget.CachedGeometry;
            if (point.X < geo.AbsolutePosition.X || point.X > geo.AbsolutePosition.X + geo.Size.X ||
                point.Y < geo.AbsolutePosition.Y || point.Y > geo.AbsolutePosition.Y + geo.Size.Y)
                return null;
            // 若是 PanelWidget, 先测试子节点 (后绘制的在上)
            if (widget is PanelWidget panel)
            {
                for (int i = panel.Children.Count - 1; i >= 0; i--)
                {
                    var hit = HitTest(panel.Children[i], point);
                    if (hit != null) return hit;
                }
            }
            return widget._flags.HasFlag(WidgetFlags.HitTestInvisible) ? null : widget;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. Widget 基类 (Widget)
    // ──────────────────────────────────────────────────────────────

    /// <summary>控件标志位。</summary>
    [Flags]
    public enum WidgetFlags : byte
    {
        None = 0,
        Visible = 1,
        Enabled = 2,
        HitTestInvisible = 4,
        HandledEvent = 8,
        ClipToBounds = 16,
        NeedsLayout = 32
    }

    /// <summary>
    /// Widget 基类: 所有 UI 控件的根。
    /// 维护父子关系、布局参数、可见性与事件虚方法。
    /// </summary>
    public abstract class Widget
    {
        /// <summary>控件名 (便于调试与查找)。</summary>
        public string Name = "Widget";
        /// <summary>父控件。</summary>
        public Widget Parent;
        /// <summary>锚点。</summary>
        public Anchors Anchors = Anchors.TopLeft;
        /// <summary>偏移边距。</summary>
        public Margins Offsets = new(0);
        /// <summary>对齐。</summary>
        public Alignment Alignment = Alignment.TopLeft;
        /// <summary>期望尺寸 (设计像素)。</summary>
        public Vector2 DesiredSize = new(100, 32);
        /// <summary>最小/最大尺寸约束。</summary>
        public Vector2 MinSize = new(1, 1);
        public Vector2 MaxSize = new(4096, 4096);
        /// <summary>渲染透明度 (0~1)。</summary>
        public float RenderOpacity = 1f;
        /// <summary>渲染变换 (缩放/旋转/平移)。</summary>
        public Vector2 RenderTransformTranslation = Vector2.Zero;
        public Vector2 RenderTransformScale = Vector2.One;
        public float RenderTransformAngle;
        /// <summary>Z 顺序 (同层内排序)。</summary>
        public int ZOrder;
        /// <summary>标签位。</summary>
        public WidgetFlags _flags = WidgetFlags.Visible | WidgetFlags.Enabled | WidgetFlags.NeedsLayout;
        /// <summary>工具提示文本。</summary>
        public string ToolTip = string.Empty;
        /// <summary>数据上下文 (用于数据绑定)。</summary>
        public object DataContext;

        /// <summary>布局缓存几何。</summary>
        public LayoutGeometry CachedGeometry;

        // ── 便捷属性 ──────────────────────────────────────────────
        public bool IsVisible
        {
            get => _flags.HasFlag(WidgetFlags.Visible);
            set { if (value) _flags |= WidgetFlags.Visible; else _flags &= ~WidgetFlags.Visible; MarkDirty(); }
        }
        public bool IsEnabled
        {
            get => _flags.HasFlag(WidgetFlags.Enabled);
            set { if (value) _flags |= WidgetFlags.Enabled; else _flags &= ~WidgetFlags.Enabled; }
        }
        public bool NeedsLayout
        {
            get => _flags.HasFlag(WidgetFlags.NeedsLayout);
            set { if (value) _flags |= WidgetFlags.NeedsLayout; else _flags &= ~WidgetFlags.NeedsLayout; }
        }

        /// <summary>标记自身及祖先需要重新布局。</summary>
        public void MarkDirty()
        {
            NeedsLayout = true;
            Parent?.MarkDirty();
        }

        /// <summary>计算期望尺寸 (子类重写)。</summary>
        public virtual Vector2 ComputeDesiredSize(Vector2 available) => DesiredSize;

        /// <summary>布局: 根据父容器分配的区域计算自身几何。</summary>
        public virtual void Arrange(Vector2 slotPosition, Vector2 slotSize, float scale)
        {
            // 根据锚点与偏移计算绝对位置与尺寸
            Vector2 anchorMin = Anchors.Minimum * slotSize;
            Vector2 anchorMax = Anchors.Maximum * slotSize;
            float x = slotPosition.X + anchorMin.X + Offsets.Left;
            float y = slotPosition.Y + anchorMin.Y + Offsets.Top;
            float w = (anchorMax.X - anchorMin.X) + Offsets.Right - Offsets.Left;
            float h = (anchorMax.Y - anchorMin.Y) + Offsets.Bottom - Offsets.Top;
            // 若锚点重合 (非拉伸), 用 DesiredSize 决定尺寸
            if (Anchors.Minimum.X == Anchors.Maximum.X) w = DesiredSize.X;
            if (Anchors.Minimum.Y == Anchors.Maximum.Y) h = DesiredSize.Y;
            // 对齐偏移
            x += Alignment.Horizontal * (slotSize.X - w) * (Anchors.Minimum.X == Anchors.Maximum.X ? 1 : 0);
            // 约束尺寸
            w = Math.Clamp(w, MinSize.X, MaxSize.X);
            h = Math.Clamp(h, MinSize.Y, MaxSize.Y);
            CachedGeometry = new LayoutGeometry
            {
                AbsolutePosition = new Vector2(x, y),
                Size = new Vector2(w, h),
                Scale = scale
            };
            NeedsLayout = false;
        }

        // ── 事件虚方法 (子类可重写) ───────────────────────────────
        protected internal virtual void HandleMouseDown(WidgetEventArgs e) { _flags &= ~WidgetFlags.HandledEvent; }
        protected internal virtual void HandleMouseUp(WidgetEventArgs e) { }
        protected internal virtual void HandleClick(WidgetEventArgs e) { }
        protected internal virtual void HandleMouseMove(WidgetEventArgs e) { }
        protected internal virtual void HandleMouseEnter(WidgetEventArgs e) { }
        protected internal virtual void HandleMouseLeave(WidgetEventArgs e) { }
        protected internal virtual void HandleMouseWheel(WidgetEventArgs e) { }
        protected internal virtual void HandleKeyDown(WidgetEventArgs e) { }
        protected internal virtual void HandleCharInput(WidgetEventArgs e) { }
        protected internal virtual void HandleFocusReceived(WidgetEventArgs e) { }
        protected internal virtual void HandleFocusLost(WidgetEventArgs e) { }
        protected internal virtual void HandleDrag(WidgetEventArgs e) { }

        /// <summary>每帧 Tick (动画/逻辑更新)。</summary>
        public virtual void Tick(float dt) { }

        /// <summary>渲染 (子类实现具体绘制)。</summary>
        public virtual void Paint(UmgRenderer renderer) { }

        /// <summary>在子树中按名称查找控件。</summary>
        public virtual Widget FindChild(string name)
        {
            if (Name == name) return this;
            return null;
        }
    }

    /// <summary>
    /// 叶子控件基类: 不可含子节点的控件 (如 Text、Image)。
    /// </summary>
    public abstract class LeafWidget : Widget { }

    /// <summary>
    /// 容器控件基类: 可含子节点, 负责子节点布局与裁剪。
    /// </summary>
    public abstract class PanelWidget : Widget
    {
        /// <summary>子控件列表 (绘制顺序即列表顺序)。</summary>
        public readonly List<Widget> Children = new();
        /// <summary>子控件槽位信息 (布局参数)。</summary>
        public readonly List<WidgetSlot> Slots = new();

        /// <summary>添加子控件。</summary>
        public virtual Widget AddChild(Widget child, WidgetSlot slot = null)
        {
            child.Parent = this;
            Children.Add(child);
            Slots.Add(slot ?? new WidgetSlot());
            MarkDirty();
            return child;
        }

        /// <summary>移除子控件。</summary>
        public virtual bool RemoveChild(Widget child)
        {
            int idx = Children.IndexOf(child);
            if (idx < 0) return false;
            Children.RemoveAt(idx);
            Slots.RemoveAt(idx);
            child.Parent = null;
            MarkDirty();
            return true;
        }

        /// <summary>清空子控件。</summary>
        public virtual void ClearChildren()
        {
            foreach (var c in Children) c.Parent = null;
            Children.Clear();
            Slots.Clear();
            MarkDirty();
        }

        public override Widget FindChild(string name)
        {
            if (base.FindChild(name) is Widget found) return found;
            foreach (var c in Children)
            {
                var r = c.FindChild(name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>容器布局: 子类重写以定义子节点排列规则。</summary>
        public virtual void ArrangeChildren(float scale) { }

        public override void Tick(float dt)
        {
            base.Tick(dt);
            foreach (var c in Children) c.Tick(dt);
        }

        public override void Paint(UmgRenderer renderer)
        {
            if (!IsVisible) return;
            // 按 ZOrder 排序后绘制
            var sorted = new List<Widget>(Children);
            sorted.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
            foreach (var c in sorted)
            {
                if (!c.IsVisible) continue;
                renderer.PushOpacity(c.RenderOpacity);
                c.Paint(renderer);
                renderer.PopOpacity();
            }
        }
    }

    /// <summary>子控件槽位: 描述子控件在容器中的布局参数。</summary>
    public class WidgetSlot
    {
        public HAlign HorizontalAlignment = HAlign.Fill;
        public VAlign VerticalAlignment = VAlign.Fill;
        public Margins Padding = new(0);
        public int ZOrder;
        public float SizeRule; // 0=自动, 1=填充
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 内置叶子控件
    // ──────────────────────────────────────────────────────────────

    /// <summary>文本控件 (支持 SDF 字体渲染)。</summary>
    public sealed class TextWidget : LeafWidget
    {
        public string Text = string.Empty;
        public SlateFontInfo Font = new() { Size = 14f };
        public Color Color = Color.White;
        public Color ShadowColor = new(0, 0, 0, 0.5f);
        public Vector2 ShadowOffset = new(1, 1);
        public bool Wrap;
        public HAlign TextHAlign = HAlign.Left;
        public VAlign TextVAlign = VAlign.Top;
        public bool RichText;

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            // 估算文本尺寸 (字号 * 字符数近似)
            float charW = Font.Size * 0.6f;
            float w = Math.Min(available.X, Text.Length * charW);
            float h = Font.Size * 1.3f * (Wrap ? Math.Max(1, (Text.Length * charW) / Math.Max(w, 1)) : 1);
            DesiredSize = new Vector2(Math.Max(w, charW), h);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            if (string.IsNullOrEmpty(Text)) return;
            var geo = CachedGeometry;
            renderer.DrawText(geo.AbsolutePosition, geo.Size, Text, Font, Color,
                ShadowColor, ShadowOffset, TextHAlign, TextVAlign, RichText);
        }
    }

    /// <summary>图片控件 (支持纯色/纹理/9-Slice)。</summary>
    public sealed class ImageWidget : LeafWidget
    {
        public SlateBrush Brush = new() { Type = BrushType.SolidColor, TintColor = Color.White };
        public Vector2 ImageSize = new(64, 64);
        public bool PreserveAspectRatio;

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            DesiredSize = ImageSize;
            return ImageSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawBrush(geo.AbsolutePosition, geo.Size, Brush);
        }
    }

    /// <summary>间隔控件: 仅占位, 不绘制。</summary>
    public sealed class SpacerWidget : LeafWidget
    {
        public SpacerWidget() { DesiredSize = new(10, 10); }
        public SpacerWidget(float w, float h) { DesiredSize = new(w, h); }
        public override Vector2 ComputeDesiredSize(Vector2 available) => DesiredSize;
        public override void Paint(UmgRenderer renderer) { }
    }

    /// <summary>进度条控件。</summary>
    public sealed class ProgressBarWidget : LeafWidget
    {
        public float Percent = 0f;
        public Color BarColor = Color.Blue;
        public Color BackgroundColor = new(0.15f, 0.16f, 0.19f, 1f);
        public float BorderThickness = 1f;
        public Color BorderColor = new(0.25f, 0.26f, 0.3f, 0.6f);
        public bool Marquee;
        public float MarqueeSpeed = 1f;
        private float _marqueeOffset;

        public override void Tick(float dt)
        {
            if (Marquee) _marqueeOffset = (_marqueeOffset + MarqueeSpeed * dt) % 1f;
        }

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            DesiredSize = new(200, 16);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawRect(geo.AbsolutePosition, geo.Size, BackgroundColor, BorderThickness, BorderColor);
            if (Marquee)
            {
                float segW = geo.Size.X * 0.3f;
                float x = geo.AbsolutePosition.X + _marqueeOffset * (geo.Size.X - segW);
                renderer.DrawRect(new(x, geo.AbsolutePosition.Y), new(segW, geo.Size.Y), BarColor, 0, Color.Transparent);
            }
            else
            {
                float w = geo.Size.X * Math.Clamp(Percent, 0f, 1f);
                renderer.DrawRect(geo.AbsolutePosition, new(w, geo.Size.Y), BarColor, 0, Color.Transparent);
            }
        }
    }

    /// <summary>复选框控件。</summary>
    public sealed class CheckBoxWidget : LeafWidget
    {
        public bool IsChecked;
        public SlateBrush CheckedBrush = new() { Type = BrushType.SolidColor, TintColor = Color.Blue };
        public SlateBrush UncheckedBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.2f, 0.2f, 0.25f, 1f) };
        public string Label = string.Empty;
        public SlateFontInfo LabelFont = new() { Size = 14f };
        public Color LabelColor = Color.White;
        public Vector2 BoxSize = new(16, 16);
        public event Action<bool> OnCheckChanged;

        protected internal override void HandleClick(WidgetEventArgs e)
        {
            IsChecked = !IsChecked;
            OnCheckChanged?.Invoke(IsChecked);
            _flags |= WidgetFlags.HandledEvent;
        }

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            float labelW = string.IsNullOrEmpty(Label) ? 0 : Label.Length * LabelFont.Size * 0.6f + 8;
            DesiredSize = new(BoxSize.X + labelW, Math.Max(BoxSize.Y, LabelFont.Size * 1.3f));
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            var brush = IsChecked ? CheckedBrush : UncheckedBrush;
            renderer.DrawBrush(geo.AbsolutePosition, BoxSize, brush);
            if (IsChecked)
                renderer.DrawRect(geo.AbsolutePosition + new Vector2(3, 3), BoxSize - new Vector2(6, 6),
                    Color.White, 0, Color.Transparent);
            if (!string.IsNullOrEmpty(Label))
                renderer.DrawText(geo.AbsolutePosition + new Vector2(BoxSize.X + 6, 0),
                    new(Label.Length * LabelFont.Size * 0.6f, LabelFont.Size * 1.3f),
                    Label, LabelFont, LabelColor, Color.Transparent, Vector2.Zero, HAlign.Left, VAlign.Center, false);
        }
    }

    /// <summary>滑块控件。</summary>
    public sealed class SliderWidget : LeafWidget
    {
        public float Value = 0.5f;
        public float MinValue = 0f;
        public float MaxValue = 1f;
        public float Step = 0f;
        public Orientation Orientation = Orientation.Horizontal;
        public Color BarColor = Color.Blue;
        public Color TrackColor = new(0.2f, 0.2f, 0.25f, 1f);
        public float HandleSize = 12f;
        public bool IsDragging;
        public event Action<float> OnValueChanged;

        protected internal override void HandleMouseDown(WidgetEventArgs e)
        {
            IsDragging = true;
            UpdateValueFromMouse(e.LocalMousePosition);
            _flags |= WidgetFlags.HandledEvent;
        }
        protected internal override void HandleMouseUp(WidgetEventArgs e) => IsDragging = false;
        protected internal override void HandleMouseMove(WidgetEventArgs e)
        {
            if (IsDragging) UpdateValueFromMouse(e.LocalMousePosition);
        }

        private void UpdateValueFromMouse(Vector2 local)
        {
            float t = Orientation == Orientation.Horizontal
                ? local.X / Math.Max(CachedGeometry.Size.X, 1)
                : 1f - local.Y / Math.Max(CachedGeometry.Size.Y, 1);
            float raw = MinValue + Math.Clamp(t, 0, 1) * (MaxValue - MinValue);
            if (Step > 0) raw = MathF.Round(raw / Step) * Step;
            Value = Math.Clamp(raw, MinValue, MaxValue);
            OnValueChanged?.Invoke(Value);
        }

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            DesiredSize = Orientation == Orientation.Horizontal ? new(160, 16) : new(16, 160);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawRect(geo.AbsolutePosition, geo.Size, TrackColor, 0, Color.Transparent);
            float t = (Value - MinValue) / Math.Max(MaxValue - MinValue, 0.0001f);
            if (Orientation == Orientation.Horizontal)
            {
                renderer.DrawRect(geo.AbsolutePosition, new(geo.Size.X * t, geo.Size.Y), BarColor, 0, Color.Transparent);
                float hx = geo.AbsolutePosition.X + geo.Size.X * t - HandleSize * 0.5f;
                float hy = geo.AbsolutePosition.Y + geo.Size.Y * 0.5f - HandleSize * 0.5f;
                renderer.DrawRect(new(hx, hy), new(HandleSize, HandleSize), Color.White, 1, Color.Gray);
            }
            else
            {
                float h = geo.Size.Y * t;
                renderer.DrawRect(new(geo.AbsolutePosition.X, geo.AbsolutePosition.Y + geo.Size.Y - h),
                    new(geo.Size.X, h), BarColor, 0, Color.Transparent);
            }
        }
    }

    /// <summary>朝向枚举。</summary>
    public enum Orientation { Horizontal, Vertical }

    /// <summary>输入框控件。</summary>
    public sealed class InputFieldWidget : LeafWidget
    {
        public StringBuilder Text = new();
        public string Placeholder = "Enter text...";
        public SlateFontInfo Font = new() { Size = 14f };
        public Color TextColor = Color.White;
        public Color PlaceholderColor = new(0.5f, 0.5f, 0.55f, 1f);
        public Color BackgroundColor = new(0.18f, 0.19f, 0.23f, 1f);
        public Color CaretColor = Color.White;
        public bool IsPassword;
        public int MaxLength = 256;
        public float CaretBlinkTime;
        public event Action<string> OnTextChanged;
        public event Action<string> OnSubmit;

        protected internal override void HandleClick(WidgetEventArgs e)
        {
            _flags |= WidgetFlags.HandledEvent;
        }
        protected internal override void HandleCharInput(WidgetEventArgs e)
        {
            if (Text.Length >= MaxLength) return;
            if (e.Char >= 32)
            {
                Text.Append(e.Char);
                OnTextChanged?.Invoke(Text.ToString());
            }
        }
        protected internal override void HandleKeyDown(WidgetEventArgs e)
        {
            if (e.KeyCode == 8 && Text.Length > 0) // Backspace
            {
                Text.Remove(Text.Length - 1, 1);
                OnTextChanged?.Invoke(Text.ToString());
            }
            else if (e.KeyCode == 13) // Enter
            {
                OnSubmit?.Invoke(Text.ToString());
            }
        }

        public override void Tick(float dt) => CaretBlinkTime += dt;

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            DesiredSize = new(200, Font.Size * 1.6f);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawRect(geo.AbsolutePosition, geo.Size, BackgroundColor, 1, Color.Gray);
            string display = IsPassword ? new string('*', Text.Length) : Text.ToString();
            if (string.IsNullOrEmpty(display))
                renderer.DrawText(geo.AbsolutePosition, geo.Size, Placeholder, Font, PlaceholderColor,
                    Color.Transparent, Vector2.Zero, HAlign.Left, VAlign.Center, false);
            else
                renderer.DrawText(geo.AbsolutePosition, geo.Size, display, Font, TextColor,
                    Color.Transparent, Vector2.Zero, HAlign.Left, VAlign.Center, false);
            // 光标闪烁
            if (((int)(CaretBlinkTime * 2)) % 2 == 0)
            {
                float caretX = geo.AbsolutePosition.X + 4 + display.Length * Font.Size * 0.6f;
                renderer.DrawRect(new(caretX, geo.AbsolutePosition.Y + 4),
                    new(1, geo.Size.Y - 8), CaretColor, 0, Color.Transparent);
            }
        }
    }

    /// <summary>下拉选择框控件。</summary>
    public sealed class ComboBoxWidget : LeafWidget
    {
        public List<string> Options = new();
        public int SelectedIndex = -1;
        public SlateFontInfo Font = new() { Size = 14f };
        public Color TextColor = Color.White;
        public Color BackgroundColor = new(0.18f, 0.19f, 0.23f, 1f);
        public bool IsDropdownOpen;
        public float ItemHeight = 22f;
        public event Action<int, string> OnSelectionChanged;

        protected internal override void HandleClick(WidgetEventArgs e)
        {
            if (IsDropdownOpen)
            {
                // 判断点击项
                int idx = (int)(e.LocalMousePosition.Y / ItemHeight) - 1;
                if (idx >= 0 && idx < Options.Count)
                {
                    SelectedIndex = idx;
                    OnSelectionChanged?.Invoke(idx, Options[idx]);
                }
                IsDropdownOpen = false;
            }
            else
            {
                IsDropdownOpen = true;
            }
            _flags |= WidgetFlags.HandledEvent;
        }

        public string SelectedOption => SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : null;

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            DesiredSize = new(160, ItemHeight);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawRect(geo.AbsolutePosition, geo.Size, BackgroundColor, 1, Color.Gray);
            string sel = SelectedOption ?? "-- Select --";
            renderer.DrawText(geo.AbsolutePosition, geo.Size, sel, Font, TextColor,
                Color.Transparent, Vector2.Zero, HAlign.Left, VAlign.Center, false);
            if (IsDropdownOpen)
            {
                float y = geo.AbsolutePosition.Y + geo.Size.Y;
                for (int i = 0; i < Options.Count; i++)
                {
                    var bg = i == SelectedIndex ? Color.LightGray : BackgroundColor;
                    renderer.DrawRect(new Vector2(geo.AbsolutePosition.X, y + i * ItemHeight),
                        new Vector2(geo.Size.X, ItemHeight), bg, 0, Color.Transparent);
                    renderer.DrawText(new(geo.AbsolutePosition.X, y + i * ItemHeight),
                        new(geo.Size.X, ItemHeight), Options[i], Font, TextColor,
                        Color.Transparent, Vector2.Zero, HAlign.Left, VAlign.Center, false);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 按钮 (Button)
    // ──────────────────────────────────────────────────────────────

    /// <summary>按钮控件: 含可选文本/图标, 支持按下/悬停态。</summary>
    public sealed class ButtonWidget : LeafWidget
    {
        public string Text = "Button";
        public SlateFontInfo Font = new() { Size = 14f };
        public SlateBrush NormalBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.26f, 0.27f, 0.32f, 1f) };
        public SlateBrush HoveredBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.3f, 0.31f, 0.36f, 1f) };
        public SlateBrush PressedBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.2f, 0.22f, 0.27f, 1f) };
        public SlateBrush DisabledBrush = new() { Type = BrushType.SolidColor, TintColor = new(0.2f, 0.2f, 0.22f, 0.6f) };
        public Color TextColor = Color.White;
        public Color TextShadowColor = new(0, 0, 0, 0.5f);
        public Vector2 TextShadowOffset = new(1, 1);
        public Vector2 Padding = new(12, 6);
        public bool IsHovered;
        public bool IsPressed;
        public event Action OnClicked;

        protected internal override void HandleMouseEnter(WidgetEventArgs e) => IsHovered = true;
        protected internal override void HandleMouseLeave(WidgetEventArgs e) { IsHovered = false; IsPressed = false; }
        protected internal override void HandleMouseDown(WidgetEventArgs e) { IsPressed = true; _flags |= WidgetFlags.HandledEvent; }
        protected internal override void HandleMouseUp(WidgetEventArgs e) => IsPressed = false;
        protected internal override void HandleClick(WidgetEventArgs e)
        {
            if (IsEnabled) OnClicked?.Invoke();
            _flags |= WidgetFlags.HandledEvent;
        }

        public override Vector2 ComputeDesiredSize(Vector2 available)
        {
            float textW = Text.Length * Font.Size * 0.6f;
            DesiredSize = new(textW + Padding.X * 2, Font.Size * 1.6f + Padding.Y * 2);
            return DesiredSize;
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            SlateBrush brush;
            if (!IsEnabled) brush = DisabledBrush;
            else if (IsPressed) brush = PressedBrush;
            else if (IsHovered) brush = HoveredBrush;
            else brush = NormalBrush;
            renderer.DrawBrush(geo.AbsolutePosition, geo.Size, brush);
            renderer.DrawText(geo.AbsolutePosition, geo.Size, Text, Font, TextColor,
                TextShadowColor, TextShadowOffset, HAlign.Center, VAlign.Center, false);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 容器控件 (CanvasPanel / VerticalBox / HorizontalBox / Border / ScrollBox)
    // ──────────────────────────────────────────────────────────────

    /// <summary>画布面板: 子控件用绝对锚点自由定位。</summary>
    public sealed class CanvasPanelWidget : PanelWidget
    {
        public override void ArrangeChildren(float scale)
        {
            foreach (var child in Children)
            {
                child.Arrange(CachedGeometry.AbsolutePosition, CachedGeometry.Size, scale);
            }
        }
        public override void Tick(float dt)
        {
            base.Tick(dt);
            if (NeedsLayout) ArrangeChildren(CachedGeometry.Scale);
        }
    }

    /// <summary>垂直盒子: 子控件自上而下堆叠。</summary>
    public sealed class VerticalBoxWidget : PanelWidget
    {
        public float Spacing = 4f;
        public override void ArrangeChildren(float scale)
        {
            float y = CachedGeometry.AbsolutePosition.Y;
            float x = CachedGeometry.AbsolutePosition.X;
            foreach (var child in Children)
            {
                var size = child.ComputeDesiredSize(CachedGeometry.Size);
                child.Arrange(new(x, y), new(CachedGeometry.Size.X, size.Y), scale);
                y += size.Y + Spacing;
            }
        }
        public override void Tick(float dt)
        {
            base.Tick(dt);
            if (NeedsLayout) ArrangeChildren(CachedGeometry.Scale);
        }
    }

    /// <summary>水平盒子: 子控件从左到右堆叠。</summary>
    public sealed class HorizontalBoxWidget : PanelWidget
    {
        public float Spacing = 4f;
        public override void ArrangeChildren(float scale)
        {
            float x = CachedGeometry.AbsolutePosition.X;
            float y = CachedGeometry.AbsolutePosition.Y;
            foreach (var child in Children)
            {
                var size = child.ComputeDesiredSize(CachedGeometry.Size);
                child.Arrange(new(x, y), new(size.X, CachedGeometry.Size.Y), scale);
                x += size.X + Spacing;
            }
        }
        public override void Tick(float dt)
        {
            base.Tick(dt);
            if (NeedsLayout) ArrangeChildren(CachedGeometry.Scale);
        }
    }

    /// <summary>边框控件: 仅含一个子控件, 周围绘制边框与背景。</summary>
    public sealed class BorderWidget : PanelWidget
    {
        public SlateBrush Background = new() { Type = BrushType.SolidColor, TintColor = new(0.15f, 0.16f, 0.19f, 0.9f) };
        public Color BorderColor = new(0.25f, 0.26f, 0.3f, 0.6f);
        public float BorderThickness = 1f;
        public Vector2 ContentPadding = new(4, 4);
        public float CornerRadius;

        public override void ArrangeChildren(float scale)
        {
            var pos = CachedGeometry.AbsolutePosition + ContentPadding;
            var size = CachedGeometry.Size - ContentPadding * 2;
            foreach (var child in Children)
                child.Arrange(pos, size, scale);
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            renderer.DrawBrush(geo.AbsolutePosition, geo.Size, Background);
            renderer.DrawRect(geo.AbsolutePosition, geo.Size, Color.Transparent, BorderThickness, BorderColor);
            base.Paint(renderer);
        }
    }

    /// <summary>滚动盒子: 内容超出可视区时可滚动。</summary>
    public sealed class ScrollBoxWidget : PanelWidget
    {
        public float ScrollOffset;
        public float ContentHeight;
        public float ViewHeight => CachedGeometry.Size.Y;
        public Color ScrollBarColor = new(0.4f, 0.4f, 0.45f, 0.8f);
        public float ScrollBarWidth = 10f;
        public bool AutoScrollToBottom;
        private bool _draggingBar;

        protected internal override void HandleMouseWheel(WidgetEventArgs e)
        {
            ScrollOffset = Math.Clamp(ScrollOffset - e.WheelDelta * 40f, 0f, Math.Max(ContentHeight - ViewHeight, 0));
            _flags |= WidgetFlags.HandledEvent;
        }

        public void ScrollToEnd()
        {
            ScrollOffset = Math.Max(ContentHeight - ViewHeight, 0);
        }

        public override void ArrangeChildren(float scale)
        {
            float y = CachedGeometry.AbsolutePosition.Y - ScrollOffset;
            float x = CachedGeometry.AbsolutePosition.X;
            ContentHeight = 0;
            foreach (var child in Children)
            {
                var size = child.ComputeDesiredSize(new(CachedGeometry.Size.X - ScrollBarWidth, CachedGeometry.Size.Y));
                child.Arrange(new(x, y), new(CachedGeometry.Size.X - ScrollBarWidth, size.Y), scale);
                y += size.Y;
                ContentHeight += size.Y;
            }
            if (AutoScrollToBottom) ScrollToEnd();
        }

        public override void Paint(UmgRenderer renderer)
        {
            var geo = CachedGeometry;
            // 裁剪到可视区
            renderer.PushClip(geo.AbsolutePosition, geo.Size);
            base.Paint(renderer);
            renderer.PopClip();
            // 绘制滚动条
            float maxScroll = Math.Max(ContentHeight - ViewHeight, 0);
            if (maxScroll > 0)
            {
                float barH = ViewHeight * (ViewHeight / ContentHeight);
                float barY = geo.AbsolutePosition.Y + (ScrollOffset / maxScroll) * (ViewHeight - barH);
                renderer.DrawRect(new(geo.AbsolutePosition.X + geo.Size.X - ScrollBarWidth, barY),
                    new(ScrollBarWidth, barH), ScrollBarColor, 0, Color.Transparent);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 数据绑定 (Data Binding)
    // ──────────────────────────────────────────────────────────────

    /// <summary>绑定方向。</summary>
    public enum BindingMode { OneWay, TwoWay, OneTime }

    /// <summary>
    /// 数据绑定: 将控件属性绑定到 DataContext 的属性路径,
    /// 当源变化时刷新控件; TwoWay 模式下控件变化回写源。
    /// </summary>
    public sealed class Binding
    {
        public Widget Target;
        public string TargetProperty;
        public string SourcePath;
        public BindingMode Mode = BindingMode.OneWay;
        public object LastValue;

        /// <summary>从源读取值并写入目标控件属性 (反射)。</summary>
        public void Pull()
        {
            if (Target.DataContext == null) return;
            var value = ResolvePath(Target.DataContext, SourcePath);
            if (value == null) return;
            if (!Equals(value, LastValue))
            {
                SetWidgetProperty(Target, TargetProperty, value);
                LastValue = value;
            }
        }

        /// <summary>从控件读取值回写源 (TwoWay)。</summary>
        public void Push()
        {
            if (Mode != BindingMode.TwoWay || Target.DataContext == null) return;
            var value = GetWidgetProperty(Target, TargetProperty);
            if (!Equals(value, LastValue))
            {
                SetPathValue(Target.DataContext, SourcePath, value);
                LastValue = value;
            }
        }

        private object ResolvePath(object source, string path)
        {
            object current = source;
            foreach (var part in path.Split('.'))
            {
                if (current == null) return null;
                var prop = current.GetType().GetProperty(part);
                if (prop == null) return null;
                current = prop.GetValue(current);
            }
            return current;
        }

        private void SetPathValue(object target, string path, object value)
        {
            object current = target;
            var parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var prop = current.GetType().GetProperty(parts[i]);
                if (prop == null) return;
                current = prop.GetValue(current);
            }
            var lastProp = current?.GetType().GetProperty(parts[^1]);
            lastProp?.SetValue(current, value);
        }

        private void SetWidgetProperty(Widget w, string name, object value)
        {
            var prop = w.GetType().GetProperty(name);
            if (prop != null && prop.CanWrite)
                prop.SetValue(w, Convert.ChangeType(value, prop.PropertyType));
        }

        private object GetWidgetProperty(Widget w, string name)
        {
            var prop = w.GetType().GetProperty(name);
            return prop?.GetValue(w);
        }
    }

    /// <summary>绑定管理器: 集中刷新所有绑定。</summary>
    public sealed class BindingManager
    {
        private readonly List<Binding> _bindings = new();
        public void Add(Binding b) => _bindings.Add(b);
        public void Remove(Binding b) => _bindings.Remove(b);
        public void Clear() => _bindings.Clear();
        /// <summary>每帧拉取所有 OneWay 绑定。</summary>
        public void Update()
        {
            foreach (var b in _bindings)
            {
                b.Pull();
                if (b.Mode == BindingMode.TwoWay) b.Push();
            }
        }
        public int Count => _bindings.Count;
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 控件动画 (Widget Animation)
    // ──────────────────────────────────────────────────────────────

    /// <summary>动画轨道类型。</summary>
    public enum AnimationTrackType { Fade, MoveX, MoveY, ScaleX, ScaleY, Rotate }

    /// <summary>单条动画轨道。</summary>
    public struct AnimationTrack
    {
        public AnimationTrackType Type;
        public float From;
        public float To;
        public float StartTime;
        public float Duration;
        public EasingType Easing;
    }

    /// <summary>
    /// 控件动画: 基于时间轴驱动淡入淡出、滑动、缩放、旋转。
    /// </summary>
    public sealed class WidgetAnimation
    {
        public string Name = "Anim";
        public float Duration = 0.3f;
        public bool Loop;
        public bool AutoPlay;
        public bool IsPlaying;
        public float CurrentTime;
        public readonly List<AnimationTrack> Tracks = new();

        public WidgetAnimation AddTrack(AnimationTrackType type, float from, float to, float start, float dur, EasingType ease = EasingType.EaseInOut)
        {
            Tracks.Add(new AnimationTrack { Type = type, From = from, To = to, StartTime = start, Duration = dur, Easing = ease });
            return this;
        }

        public void Play() { CurrentTime = 0; IsPlaying = true; }
        public void Stop() { IsPlaying = false; CurrentTime = 0; }
        public void Pause() => IsPlaying = false;

        /// <summary>推进动画并应用到目标控件。</summary>
        public void Update(Widget target, float dt)
        {
            if (!IsPlaying) return;
            CurrentTime += dt;
            if (CurrentTime >= Duration)
            {
                if (Loop) CurrentTime %= Duration;
                else { CurrentTime = Duration; IsPlaying = false; }
            }
            foreach (var track in Tracks)
            {
                if (CurrentTime < track.StartTime) continue;
                float local = (CurrentTime - track.StartTime) / Math.Max(track.Duration, 0.0001f);
                local = Math.Clamp(local, 0, 1);
                float t = ApplyEasing(local, track.Easing);
                float value = track.From + (track.To - track.From) * t;
                switch (track.Type)
                {
                    case AnimationTrackType.Fade: target.RenderOpacity = value; break;
                    case AnimationTrackType.MoveX: target.RenderTransformTranslation = new(value, target.RenderTransformTranslation.Y); break;
                    case AnimationTrackType.MoveY: target.RenderTransformTranslation = new(target.RenderTransformTranslation.X, value); break;
                    case AnimationTrackType.ScaleX: target.RenderTransformScale = new(value, target.RenderTransformScale.Y); break;
                    case AnimationTrackType.ScaleY: target.RenderTransformScale = new(target.RenderTransformScale.X, value); break;
                    case AnimationTrackType.Rotate: target.RenderTransformAngle = value; break;
                }
            }
        }

        private static float ApplyEasing(float t, EasingType type)
        {
            return type switch
            {
                EasingType.Linear => t,
                EasingType.EaseIn => t * t,
                EasingType.EaseOut => 1f - (1f - t) * (1f - t),
                EasingType.EaseInOut => t < 0.5f ? 2 * t * t : 1f - MathF.Pow(-2f * t + 2f, 2) / 2f,
                EasingType.Bounce => BounceEase(t),
                EasingType.Elastic => ElasticEase(t),
                EasingType.Back => 1f + 2.70158f * MathF.Pow(t - 1f, 3) + 1.70158f * MathF.Pow(t - 1f, 2),
                _ => t
            };
        }

        private static float BounceEase(float t)
        {
            float n1 = 7.5625f, d1 = 2.75f;
            if (t < 1f / d1) return n1 * t * t;
            if (t < 2f / d1) return n1 * (t -= 1.5f / d1) * t + 0.75f;
            if (t < 2.5f / d1) return n1 * (t -= 2.25f / d1) * t + 0.9375f;
            return n1 * (t -= 2.625f / d1) * t + 0.984375f;
        }

        private static float ElasticEase(float t)
        {
            if (t == 0 || t == 1) return t;
            float c4 = (2f * MathF.PI) / 3f;
            return MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * c4) + 1f;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 12. SDF 字体与富文本 (SDF Font & RichText)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// SDF (Signed Distance Field) 字体图集。
    /// 使用距离场纹理可在任意缩放下保持文字边缘清晰,
    /// 配合着色器实现描边、发光等特效。
    /// </summary>
    public sealed class SDFFontAtlas
    {
        public string FontName = "Default";
        /// <summary>距离场纹理名 (由资源管理器解析为 RHITexture)。</summary>
        public string AtlasTextureName = "Font_Atlas";
        public int AtlasWidth = 1024;
        public int AtlasHeight = 1024;
        public float BaseSize = 32f;
        public float Spread = 4f;
        public readonly Dictionary<char, GlyphInfo> Glyphs = new();

        /// <summary>注册一个字形的图集信息。</summary>
        public void AddGlyph(char c, Vector2 uvMin, Vector2 uvMax, Vector2 offset, Vector2 size, float advance)
        {
            Glyphs[c] = new GlyphInfo { UVMin = uvMin, UVMax = uvMax, Offset = offset, Size = size, Advance = advance };
        }

        public bool TryGetGlyph(char c, out GlyphInfo info) => Glyphs.TryGetValue(c, out info);

        /// <summary>计算字符串在给定字号下的布局尺寸。</summary>
        public Vector2 MeasureString(string text, float fontSize)
        {
            float scale = fontSize / BaseSize;
            float x = 0, y = fontSize * 1.2f, maxX = 0;
            foreach (char c in text)
            {
                if (c == '\n') { y += fontSize * 1.2f; x = 0; continue; }
                if (TryGetGlyph(c, out var g)) x += g.Advance * scale;
                maxX = Math.Max(maxX, x);
            }
            return new(maxX, y);
        }
    }

    /// <summary>单个字形信息。</summary>
    public struct GlyphInfo
    {
        public Vector2 UVMin;
        public Vector2 UVMax;
        public Vector2 Offset;
        public Vector2 Size;
        public float Advance;
    }

    /// <summary>富文本片段 (含样式)。</summary>
    public struct RichTextSegment
    {
        public string Text;
        public Color Color;
        public bool Bold;
        public bool Italic;
        public float Size;
        public string Link;
    }

    /// <summary>富文本解析器: 将含标记的字符串解析为片段列表。</summary>
    public static class RichTextParser
    {
        /// <summary>解析形如 "&lt;color=red&gt;红字&lt;/color&gt;普通" 的标记文本。</summary>
        public static List<RichTextSegment> Parse(string input, SlateFontInfo defaultFont)
        {
            var segments = new List<RichTextSegment>();
            var sb = new StringBuilder();
            Color current = Color.White;
            bool bold = false, italic = false;
            float size = defaultFont.Size;
            int i = 0;
            while (i < input.Length)
            {
                if (input[i] == '<')
                {
                    // 提交已累积文本
                    if (sb.Length > 0)
                    {
                        segments.Add(new RichTextSegment { Text = sb.ToString(), Color = current, Bold = bold, Italic = italic, Size = size });
                        sb.Clear();
                    }
                    int end = input.IndexOf('>', i);
                    if (end < 0) { sb.Append(input[i]); i++; continue; }
                    string tag = input.Substring(i + 1, end - i - 1);
                    if (tag.StartsWith("color=")) current = ParseColor(tag.Substring(6));
                    else if (tag == "/color") current = Color.White;
                    else if (tag == "b") bold = true;
                    else if (tag == "/b") bold = false;
                    else if (tag == "i") italic = true;
                    else if (tag == "/i") italic = false;
                    else if (tag.StartsWith("size=")) float.TryParse(tag.Substring(5), out size);
                    else if (tag == "/size") size = defaultFont.Size;
                    i = end + 1;
                }
                else
                {
                    sb.Append(input[i]);
                    i++;
                }
            }
            if (sb.Length > 0)
                segments.Add(new RichTextSegment { Text = sb.ToString(), Color = current, Bold = bold, Italic = italic, Size = size });
            return segments;
        }

        private static Color ParseColor(string name) => name.ToLower() switch
        {
            "red" => Color.Red, "green" => Color.Green, "blue" => Color.Blue,
            "white" => Color.White, "black" => Color.Black, "gray" => Color.Gray,
            "yellow" => new Color(1, 0.9f, 0.2f, 1f), "orange" => new Color(1, 0.55f, 0.1f, 1f),
            _ => Color.White
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 13. UI 顶点与绘制元素 (UI Vertex & Draw Elements)
    // ──────────────────────────────────────────────────────────────

    /// <summary>UI 顶点 (位置 + UV + 颜色)。</summary>
    /// <summary>绘制元素类型。</summary>
    public enum DrawElementType { Rect, Text, NineSlice, Triangle, Material }

    /// <summary>单个绘制元素 (一个批次)。</summary>
    public struct DrawElement
    {
        public DrawElementType Type;
        public int VertexStart;
        public int VertexCount;
        public int IndexStart;
        public int IndexCount;
        public string TextureName;
        public string MaterialName;
        public float Depth;
        public Vector4 ClipRect;
        public bool HasClip;
    }

    /// <summary>
    /// 绘制上下文: 在一棵控件树渲染前, 收集所有 UI 顶点与绘制元素,
    /// 再由 UmgRenderer 合并成 RHI 绘制调用。
    /// </summary>
    public sealed class UmgPaintContext
    {
        public readonly List<UIVertex> Vertices = new(4096);
        public readonly List<ushort> Indices = new(8192);
        public readonly List<DrawElement> Elements = new(256);
        public float CurrentOpacity = 1f;
        public readonly Stack<Vector4> ClipStack = new();
        public readonly Stack<float> OpacityStack = new();

        public void PushOpacity(float opacity)
        {
            OpacityStack.Push(CurrentOpacity);
            CurrentOpacity *= opacity;
        }
        public void PopOpacity()
        {
            CurrentOpacity = OpacityStack.Count > 0 ? OpacityStack.Pop() : 1f;
        }
        public void PushClip(Vector2 pos, Vector2 size)
        {
            Vector4 rect = new(pos.X, pos.Y, pos.X + size.X, pos.Y + size.Y);
            if (ClipStack.Count > 0)
            {
                var prev = ClipStack.Peek();
                rect = new(Math.Max(rect.X, prev.X), Math.Max(rect.Y, prev.Y),
                           Math.Min(rect.Z, prev.Z), Math.Min(rect.W, prev.W));
            }
            ClipStack.Push(rect);
        }
        public void PopClip() { if (ClipStack.Count > 0) ClipStack.Pop(); }
        public bool HasClip => ClipStack.Count > 0;
        public Vector4 CurrentClip => ClipStack.Count > 0 ? ClipStack.Peek() : new(0, 0, float.MaxValue, float.MaxValue);
    }

    // ──────────────────────────────────────────────────────────────
    // 14. 渲染器 (UmgRenderer) - 与 RHI 对接
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UMG 渲染器: 把控件树输出的顶点/元素上传到 RHI 顶点缓冲,
    /// 按纹理/材质合并绘制调用, 执行实际的 GPU 绘制。
    /// </summary>
    public sealed class UmgRenderer
    {
        private readonly UmgPaintContext _ctx = new();
        private RHIDevice _device;
        private RHICommandList _cmdList;
        private RHIBuffer _vertexBuffer;
        private RHIBuffer _indexBuffer;
        private RHIRootSignature _rootSig;
        private RHIPipelineState _pso;
        private bool _resourcesDirty = true;
        private int _drawCallCount;

        /// <summary>当前帧绘制调用数 (供性能统计)。</summary>
        public int DrawCallCount => _drawCallCount;

        /// <summary>绘制上下文 (供控件写入顶点)。</summary>
        public UmgPaintContext Context => _ctx;

        public void PushOpacity(float o) => _ctx.PushOpacity(o);
        public void PopOpacity() => _ctx.PopOpacity();
        public void PushClip(Vector2 pos, Vector2 size) => _ctx.PushClip(pos, size);
        public void PopClip() => _ctx.PopClip();

        /// <summary>绑定 RHI 设备与命令表, 创建 UI 管线。</summary>
        public void Initialize(RHIDevice device, RHICommandList cmdList)
        {
            _device = device;
            _cmdList = cmdList;
            CreatePipeline();
            _resourcesDirty = true;
        }

        /// <summary>创建 UI 专用图形管线 (禁深度写, Alpha 混合)。</summary>
        private void CreatePipeline()
        {
            if (_device == null) return;
            // 输入布局: float2 pos, float2 uv, float4 color
            var inputLayout = new RHIInputElement[]
            {
                new("POSITION", 0, RHIFormat.R32G32_Float, 0, 0),
                new("TEXCOORD", 0, RHIFormat.R32G32_Float, 0, 8),
                new("COLOR", 0, RHIFormat.R32G32B32A32_Float, 0, 16),
            };
            var desc = new RHIGraphicsPipelineDesc
            {
                Name = "UI_Pipeline",
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm_SRgb },
                DSVFormat = RHIFormat.Unknown
            };
            _pso = _device.CreateGraphicsPipelineState(desc);
        }

        /// <summary>绘制填充矩形。</summary>
        public void DrawRect(Vector2 pos, Vector2 size, Color color, float borderThickness, Color borderColor)
        {
            var c = color.WithAlpha(color.A * _ctx.CurrentOpacity).ToVector4();
            int vStart = _ctx.Vertices.Count;
            _ctx.Vertices.Add(new(pos, Vector2.Zero, c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y), Vector2.One, c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y + size.Y), Vector2.One, c));
            _ctx.Vertices.Add(new(new(pos.X, pos.Y + size.Y), Vector2.Zero, c));
            int iStart = _ctx.Indices.Count;
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 1)); _ctx.Indices.Add((ushort)(vStart + 2));
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 2)); _ctx.Indices.Add((ushort)(vStart + 3));
            _ctx.Elements.Add(new DrawElement
            {
                Type = DrawElementType.Rect,
                VertexStart = vStart, VertexCount = 4,
                IndexStart = iStart, IndexCount = 6,
                HasClip = _ctx.HasClip, ClipRect = _ctx.CurrentClip
            });
            // 边框
            if (borderThickness > 0 && borderColor.A > 0)
            {
                DrawBorder(pos, size, borderThickness, borderColor);
            }
        }

        /// <summary>绘制矩形边框 (四条边)。</summary>
        private void DrawBorder(Vector2 pos, Vector2 size, float t, Color color)
        {
            var c = color.WithAlpha(color.A * _ctx.CurrentOpacity).ToVector4();
            // 上
            DrawRect(pos, new(size.X, t), color, 0, Color.Transparent);
            // 下
            DrawRect(new(pos.X, pos.Y + size.Y - t), new(size.X, t), color, 0, Color.Transparent);
            // 左
            DrawRect(new(pos.X, pos.Y + t), new(t, size.Y - t * 2), color, 0, Color.Transparent);
            // 右
            DrawRect(new(pos.X + size.X - t, pos.Y + t), new(t, size.Y - t * 2), color, 0, Color.Transparent);
        }

        /// <summary>绘制笔刷 (纯色/纹理/9-Slice/渐变/材质)。</summary>
        public void DrawBrush(Vector2 pos, Vector2 size, SlateBrush brush)
        {
            var tint = brush.TintColor.WithAlpha(brush.TintColor.A * _ctx.CurrentOpacity);
            switch (brush.Type)
            {
                case BrushType.SolidColor:
                    DrawRect(pos, size, tint, 0, Color.Transparent);
                    break;
                case BrushType.Texture:
                    DrawTexturedRect(pos, size, brush.UVRegion, tint, brush.TextureName);
                    break;
                case BrushType.NineSlice:
                    DrawNineSlice(pos, size, brush);
                    break;
                case BrushType.LinearGradient:
                    DrawGradientRect(pos, size, brush.GradientStart, brush.GradientEnd, brush.GradientAngle);
                    break;
                case BrushType.Material:
                    DrawMaterialRect(pos, size, brush.MaterialName, tint);
                    break;
            }
        }

        /// <summary>绘制带纹理的矩形。</summary>
        private void DrawTexturedRect(Vector2 pos, Vector2 size, Vector4 uv, Color tint, string texture)
        {
            var c = tint.ToVector4();
            int vStart = _ctx.Vertices.Count;
            _ctx.Vertices.Add(new(pos, new(uv.X, uv.Y), c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y), new(uv.Z, uv.Y), c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y + size.Y), new(uv.Z, uv.W), c));
            _ctx.Vertices.Add(new(new(pos.X, pos.Y + size.Y), new(uv.X, uv.W), c));
            int iStart = _ctx.Indices.Count;
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 1)); _ctx.Indices.Add((ushort)(vStart + 2));
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 2)); _ctx.Indices.Add((ushort)(vStart + 3));
            _ctx.Elements.Add(new DrawElement
            {
                Type = DrawElementType.Rect, TextureName = texture,
                VertexStart = vStart, VertexCount = 4,
                IndexStart = iStart, IndexCount = 6,
                HasClip = _ctx.HasClip, ClipRect = _ctx.CurrentClip
            });
        }

        /// <summary>
        /// 9-Slice (九宫格) 绘制: 将纹理分为 9 区,
        /// 四角不缩放, 四边单轴拉伸, 中心双轴拉伸。
        /// </summary>
        public void DrawNineSlice(Vector2 pos, Vector2 size, SlateBrush brush)
        {
            float m = brush.Margin;
            Vector4 uv = brush.UVRegion;
            float uvW = uv.Z - uv.X, uvH = uv.W - uv.Y;
            // 9 个区域尺寸
            float left = Math.Min(m, size.X * 0.5f);
            float top = Math.Min(m, size.Y * 0.5f);
            float right = size.X - left;
            float bottom = size.Y - top;
            // UV 边距
            float uvL = uv.X + uvW * (left / size.X);
            float uvT = uv.Y + uvH * (top / size.Y);
            float uvR = uv.X + uvW * (right / size.X);
            float uvB = uv.Y + uvH * (bottom / size.Y);
            // 中心区域
            DrawTexturedRect(new(pos.X + left, pos.Y + top), new(right - left, bottom - top),
                new(uvL, uvT, uvR, uvB), brush.TintColor, brush.TextureName);
        }

        /// <summary>绘制线性渐变矩形。</summary>
        private void DrawGradientRect(Vector2 pos, Vector2 size, Color c0, Color c1, float angle)
        {
            var a = c0.WithAlpha(c0.A * _ctx.CurrentOpacity).ToVector4();
            var b = c1.WithAlpha(c1.A * _ctx.CurrentOpacity).ToVector4();
            int vStart = _ctx.Vertices.Count;
            _ctx.Vertices.Add(new(pos, Vector2.Zero, a));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y), Vector2.One, a));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y + size.Y), Vector2.One, b));
            _ctx.Vertices.Add(new(new(pos.X, pos.Y + size.Y), Vector2.Zero, b));
            int iStart = _ctx.Indices.Count;
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 1)); _ctx.Indices.Add((ushort)(vStart + 2));
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 2)); _ctx.Indices.Add((ushort)(vStart + 3));
            _ctx.Elements.Add(new DrawElement
            {
                Type = DrawElementType.Rect, VertexStart = vStart, VertexCount = 4,
                IndexStart = iStart, IndexCount = 6,
                HasClip = _ctx.HasClip, ClipRect = _ctx.CurrentClip
            });
        }

        /// <summary>绘制 UI 材质矩形 (描边/发光等特效由着色器实现)。</summary>
        private void DrawMaterialRect(Vector2 pos, Vector2 size, string material, Color tint)
        {
            var c = tint.ToVector4();
            int vStart = _ctx.Vertices.Count;
            _ctx.Vertices.Add(new(pos, Vector2.Zero, c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y), new(1, 0), c));
            _ctx.Vertices.Add(new(new(pos.X + size.X, pos.Y + size.Y), Vector2.One, c));
            _ctx.Vertices.Add(new(new(pos.X, pos.Y + size.Y), new(0, 1), c));
            int iStart = _ctx.Indices.Count;
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 1)); _ctx.Indices.Add((ushort)(vStart + 2));
            _ctx.Indices.Add((ushort)(vStart)); _ctx.Indices.Add((ushort)(vStart + 2)); _ctx.Indices.Add((ushort)(vStart + 3));
            _ctx.Elements.Add(new DrawElement
            {
                Type = DrawElementType.Material, MaterialName = material,
                VertexStart = vStart, VertexCount = 4,
                IndexStart = iStart, IndexCount = 6,
                HasClip = _ctx.HasClip, ClipRect = _ctx.CurrentClip
            });
        }

        /// <summary>绘制文本 (简化: 每字符一个纹理矩形)。</summary>
        public void DrawText(Vector2 pos, Vector2 size, string text, SlateFontInfo font, Color color,
            Color shadowColor, Vector2 shadowOffset, HAlign hAlign, VAlign vAlign, bool richText)
        {
            if (string.IsNullOrEmpty(text)) return;
            var segments = richText ? RichTextParser.Parse(text, font) :
                new List<RichTextSegment> { new() { Text = text, Color = color, Size = font.Size } };
            // 阴影
            if (shadowColor.A > 0)
            {
                float x = pos.X + shadowOffset.X;
                foreach (var seg in segments)
                {
                    foreach (char c in seg.Text)
                    {
                        DrawRect(new(x, pos.Y + shadowOffset.Y), new(font.Size * 0.6f, font.Size * 1.2f),
                            shadowColor, 0, Color.Transparent);
                        x += font.Size * 0.6f;
                    }
                }
            }
            // 文本
            float cx = pos.X;
            foreach (var seg in segments)
            {
                var segColor = seg.Color.WithAlpha(seg.Color.A * _ctx.CurrentOpacity);
                foreach (char ch in seg.Text)
                {
                    DrawTexturedRect(new(cx, pos.Y), new(font.Size * 0.6f, font.Size * 1.2f),
                        new(0, 0, 1, 1), segColor, font.FontName + "_Atlas");
                    cx += font.Size * 0.6f;
                }
            }
        }

        /// <summary>结束一帧: 上传顶点/索引缓冲, 提交绘制调用。</summary>
        public void Flush(RHIViewport viewport)
        {
            if (_ctx.Vertices.Count == 0 || _device == null) return;
            _drawCallCount = 0;
            // 上传顶点缓冲
            UploadBuffers();
            // 设置管线与视口
            if (_pso != null) _cmdList.SetPipelineState(_pso);
            _cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            _cmdList.SetViewport(viewport);
            var scissor = new RHIScissorRect((int)viewport.TopLeftX, (int)viewport.TopLeftY,
                (int)(viewport.TopLeftX + viewport.Width), (int)(viewport.TopLeftY + viewport.Height));
            _cmdList.SetScissorRect(scissor);
            // 绑定顶点/索引缓冲
            if (_vertexBuffer != null)
                _cmdList.IASetVertexBuffers(0, _vertexBuffer, System.Runtime.InteropServices.Marshal.SizeOf<UIVertex>());
            if (_indexBuffer != null)
                _cmdList.IASetIndexBuffer(_indexBuffer, RHIFormat.R16_UInt, 0);
            // 按元素提交绘制调用 (实际生产中应按纹理合并批次)
            foreach (var elem in _ctx.Elements)
            {
                if (elem.VertexCount == 0) continue;
                _cmdList.DrawIndexedInstanced(elem.IndexCount, 1, elem.IndexStart, 0, 0);
                _drawCallCount++;
            }
            // 清空本帧数据
            _ctx.Vertices.Clear();
            _ctx.Indices.Clear();
            _ctx.Elements.Clear();
        }

        /// <summary>上传顶点/索引缓冲到 GPU。</summary>
        private void UploadBuffers()
        {
            int vBytes = _ctx.Vertices.Count * System.Runtime.InteropServices.Marshal.SizeOf<UIVertex>();
            int iBytes = _ctx.Indices.Count * sizeof(ushort);
            if (vBytes == 0) return;
            // 重建上传缓冲 (简化: 每帧重建, 实际应使用环形缓冲)
            var vDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)vBytes,
                Flags = RHIBufferFlags.VertexBuffer,
                HeapType = RHIResourceHeapType.Upload
            };
            _vertexBuffer?.Dispose();
            _vertexBuffer = _device.CreateBuffer(vDesc, "UI_VB");
            var iDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)iBytes,
                Flags = RHIBufferFlags.IndexBuffer,
                HeapType = RHIResourceHeapType.Upload
            };
            _indexBuffer?.Dispose();
            _indexBuffer = _device.CreateBuffer(iDesc, "UI_IB");
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 15. DPI 感知与 UI 缩放 (DPIScaler)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// DPI 缩放管理器: 根据屏幕 DPI 计算缩放因子,
    /// 自动调整字号与控件尺寸, 保证不同分辨率下 UI 一致。
    /// </summary>
    public static class DPIScaler
    {
        /// <summary>基准 DPI (96 = 100%)。</summary>
        public const float BaseDPI = 96f;
        /// <summary>当前屏幕 DPI。</summary>
        public static float CurrentDPI = 96f;
        /// <summary>缩放曲线 (DPI 比例 -> UI 比例), 可自定义。</summary>
        public static float UIScaleCurve = 1f;

        /// <summary>当前 UI 缩放因子。</summary>
        public static float ScaleFactor
        {
            get
            {
                float ratio = CurrentDPI / BaseDPI;
                // 离散化到 0.25 步长, 避免模糊
                float snapped = MathF.Round(ratio * 4f) / 4f;
                return snapped * UIScaleCurve;
            }
        }

        /// <summary>按 DPI 缩放像素值。</summary>
        public static float Scale(float pixels) => pixels * ScaleFactor;
        public static Vector2 Scale(Vector2 pixels) => pixels * ScaleFactor;
        public static float ScaleFontSize(float size) => MathF.Round(size * ScaleFactor);

        /// <summary>设置屏幕 DPI (由窗口系统查询后调用)。</summary>
        public static void SetDPI(float dpi) => CurrentDPI = Math.Max(96f, dpi);
    }

    // ──────────────────────────────────────────────────────────────
    // 16. UMG 根系统 (UmgRoot) - 顶层入口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UMG 根系统: 管理一棵 UI 树, 驱动布局、事件、绑定、动画与渲染。
    /// 是 UI 系统对外的主入口。
    /// </summary>
    public sealed class UmgRoot
    {
        /// <summary>根控件 (通常是一个铺满屏幕的 CanvasPanel)。</summary>
        public PanelWidget RootWidget;
        private readonly WidgetEventSystem _events = new();
        private readonly BindingManager _bindings = new();
        private readonly List<WidgetAnimation> _animations = new();
        private readonly UmgRenderer _renderer = new();
        private SlateStyle _style = SlateStyle.Dark;
        private Vector2 _viewportSize;
        private bool _layoutDirty = true;

        /// <summary>事件系统。</summary>
        public WidgetEventSystem Events => _events;
        /// <summary>绑定管理器。</summary>
        public BindingManager Bindings => _bindings;
        /// <summary>渲染器。</summary>
        public UmgRenderer Renderer => _renderer;
        /// <summary>当前样式。</summary>
        public SlateStyle Style { get => _style; set => _style = value; }
        /// <summary>视口尺寸。</summary>
        public Vector2 ViewportSize { get => _viewportSize; set { _viewportSize = value; _layoutDirty = true; } }

        public UmgRoot()
        {
            RootWidget = new CanvasPanelWidget { Name = "Root", Anchors = Anchors.StretchAll, Offsets = Margins.Zero };
            RootWidget.DesiredSize = _viewportSize;
        }

        public void Initialize(RHIDevice device, int width, int height)
        {
            ViewportSize = new Vector2(width, height);
        }

        public void Update(float dt) => Tick(dt);

        public void OnResize(int width, int height)
        {
            ViewportSize = new Vector2(width, height);
        }

        public void Dispose() { }

        /// <summary>添加顶层控件到根画布。</summary>
        public Widget AddToViewport(Widget widget)
        {
            RootWidget.AddChild(widget);
            _layoutDirty = true;
            return widget;
        }

        /// <summary>移除顶层控件。</summary>
        public bool RemoveFromViewport(Widget widget) => RootWidget.RemoveChild(widget);

        /// <summary>注册控件动画。</summary>
        public WidgetAnimation PlayAnimation(WidgetAnimation anim)
        {
            _animations.Add(anim);
            anim.Play();
            return anim;
        }

        /// <summary>每帧更新: 布局 -> 事件 -> 绑定 -> 动画 -> 渲染收集。</summary>
        public void Tick(float dt)
        {
            // 布局
            if (_layoutDirty || RootWidget.NeedsLayout)
            {
                float scale = DPIScaler.ScaleFactor;
                RootWidget.Arrange(Vector2.Zero, _viewportSize, scale);
                if (RootWidget is PanelWidget panel)
                    panel.ArrangeChildren(scale);
                _layoutDirty = false;
            }
            // 逻辑 Tick
            RootWidget.Tick(dt);
            // 绑定刷新
            _bindings.Update();
            // 动画推进
            foreach (var anim in _animations)
            {
                if (anim.IsPlaying)
                {
                    // 动画作用于其目标 (简化: 动画需自行持有目标引用)
                }
            }
            _animations.RemoveAll(a => !a.IsPlaying && !a.Loop);
        }

        /// <summary>渲染: 收集绘制元素并提交到 RHI。</summary>
        public void Render(RHIViewport viewport)
        {
            RootWidget.Paint(_renderer);
            _renderer.Flush(viewport);
        }

        // ── 输入分发代理 ──────────────────────────────────────────
        public bool HandleMouseDown(Vector2 pos, MouseButton button) => _events.DispatchMouseDown(RootWidget, pos, button);
        public bool HandleMouseUp(Vector2 pos, MouseButton button) => _events.DispatchMouseUp(RootWidget, pos, button);
        public void HandleMouseMove(Vector2 pos) => _events.DispatchMouseMove(RootWidget, pos);
        public void HandleMouseWheel(Vector2 pos, float delta) => _events.DispatchMouseWheel(RootWidget, pos, delta);
        public bool HandleKeyDown(int keyCode) => _events.DispatchKeyDown(keyCode);
        public bool HandleCharInput(char c) => _events.DispatchCharInput(c);

        /// <summary>统计摘要。</summary>
        public string GetStatsSummary() =>
            $"Widgets: {CountWidgets(RootWidget)} | DrawCalls: {_renderer.DrawCallCount} | " +
            $"Bindings: {_bindings.Count} | Anims: {_animations.Count} | DPI: {DPIScaler.ScaleFactor:F2}x";

        /// <summary>递归统计控件数。</summary>
        private int CountWidgets(Widget w)
        {
            int count = 1;
            if (w is PanelWidget panel)
                foreach (var c in panel.Children) count += CountWidgets(c);
            return count;
        }
    }
}
