using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.UI
{
    // ──────────────────────────────────────────────────────────────
    // CanvasPanel - 画布面板
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 画布面板 — 绝对定位子 Widget，无自动布局。
    /// 相当于 UE Canvas / Unity Canvas。
    /// 支持 Screen Space 和 World Space 两种模式。
    /// </summary>
    public class CanvasPanel : WidgetBase
    {
        public enum SpaceMode { ScreenSpace, WorldSpace }

        public SpaceMode Mode { get; set; } = SpaceMode.ScreenSpace;

        public CanvasPanel()
        {
            Name = "CanvasPanel";
        }

        public override void ArrangeChildren()
        {
            foreach (var child in Children)
            {
                // 计算锚点布局
                float parentW = Size.X;
                float parentH = Size.Y;

                float anchorLeft = AnchorMin.X * parentW;
                float anchorTop = AnchorMin.Y * parentH;
                float anchorRight = AnchorMax.X * parentW;
                float anchorBottom = AnchorMax.Y * parentH;

                float childX = CachedRect.X + anchorLeft + child.OffsetLeft;
                float childY = CachedRect.Y + anchorTop + child.OffsetTop;
                float childW = (anchorRight - anchorLeft) + child.OffsetRight - child.OffsetLeft;
                float childH = (anchorBottom - anchorTop) + child.OffsetBottom - child.OffsetTop;

                // 非拉伸模式下使用 DesiredSize
                if (child.AnchorMin.X == child.AnchorMax.X)
                    childW = child.ComputeDesiredSize(new Vector2(parentW, parentH)).X;
                if (child.AnchorMin.Y == child.AnchorMax.Y)
                    childH = child.ComputeDesiredSize(new Vector2(parentW, parentH)).Y;

                childW = Math.Clamp(childW, child.MinSize.X, child.MaxSize.X);
                childH = Math.Clamp(childH, child.MinSize.Y, child.MaxSize.Y);

                child.Arrange(new Rectangle(childX, childY, childW, childH));
            }
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            // 绘制子 Widget（按 ZOrder 排序）
            var sorted = new List<WidgetBase>(Children);
            sorted.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));

            foreach (var child in sorted)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                if (child.Opacity <= 0f) continue;

                var childRect = child.CachedRect;
                context.PushOpacity(child.Opacity);

                if (child.Clipping == ClippingMode.ClipToBounds)
                    context.PushClip(childRect);

                child.OnPaint(context, childRect);
                if (child is WidgetBase panel && panel.Children.Count > 0)
                {
                    // 递归绘制子节点（由子节点的 OnPaint 内完成）
                    // 这里子 Widget 已经在自己的 OnPaint 中处理了子节点
                }

                if (child.Clipping == ClippingMode.ClipToBounds)
                    context.PopClip();

                context.PopOpacity();
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // HorizontalBox - 水平排列容器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 水平盒子 — 子元素水平排列。
    /// 相当于 UE HorizontalBox / Unity Horizontal Layout Group。
    /// </summary>
    public class HorizontalBox : WidgetBase
    {
        public float Spacing { get; set; } = 4f;

        public enum ChildAlignment { Top, Center, Bottom }
        public ChildAlignment Alignment { get; set; } = ChildAlignment.Center;

        public enum DistributionMode { LeftToRight, RightToLeft, Center, Uniform }
        public DistributionMode Distribution { get; set; } = DistributionMode.LeftToRight;

        public HorizontalBox()
        {
            Name = "HorizontalBox";
            SizeToContent = true;
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            float totalW = 0;
            float maxH = 0;
            int visibleCount = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                var desired = child.ComputeDesiredSize(availableSize);
                totalW += desired.X;
                maxH = Math.Max(maxH, desired.Y);
                visibleCount++;
            }
            if (visibleCount > 0)
                totalW += Spacing * (visibleCount - 1);
            totalW += Padding.Left + Padding.Right;
            maxH += Padding.Top + Padding.Bottom;
            return new Vector2(totalW, maxH);
        }

        public override void ArrangeChildren()
        {
            float availableW = Size.X - Padding.Left - Padding.Right;
            float availableH = Size.Y - Padding.Top - Padding.Bottom;

            // 先收集所有可见子节点的 DesiredSize
            var visibleChildren = new List<WidgetBase>();
            var desiredSizes = new List<Vector2>();
            float totalDesiredW = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                visibleChildren.Add(child);
                var ds = child.ComputeDesiredSize(new Vector2(availableW, availableH));
                desiredSizes.Add(ds);
                totalDesiredW += ds.X;
            }
            if (visibleChildren.Count == 0) return;

            float totalSpacing = Spacing * (visibleChildren.Count - 1);
            float x = CachedRect.X + Padding.Left;

            // 按分布模式调整起始位置
            if (Distribution == DistributionMode.Center)
                x += (availableW - totalDesiredW - totalSpacing) * 0.5f;
            else if (Distribution == DistributionMode.RightToLeft)
                x = CachedRect.Right - Padding.Right - totalDesiredW - totalSpacing;

            for (int i = 0; i < visibleChildren.Count; i++)
            {
                var child = visibleChildren[i];
                var ds = desiredSizes[i];

                float childW = Math.Clamp(ds.X, child.MinSize.X, child.MaxSize.X);
                float childH = Math.Clamp(ds.Y, child.MinSize.Y, child.MaxSize.Y);

                float childY = CachedRect.Y + Padding.Top;
                if (Alignment == ChildAlignment.Center)
                    childY += (availableH - childH) * 0.5f;
                else if (Alignment == ChildAlignment.Bottom)
                    childY += availableH - childH;

                child.Arrange(new Rectangle(x, childY, childW, childH));
                x += childW + Spacing;
            }
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // VerticalBox - 垂直排列容器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 垂直盒子 — 子元素垂直排列。
    /// 相当于 UE VerticalBox / Unity Vertical Layout Group。
    /// </summary>
    public class VerticalBox : WidgetBase
    {
        public float Spacing { get; set; } = 4f;

        public enum ChildAlignment { Left, Center, Right }
        public ChildAlignment Alignment { get; set; } = ChildAlignment.Left;

        public VerticalBox()
        {
            Name = "VerticalBox";
            SizeToContent = true;
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            float maxW = 0;
            float totalH = 0;
            int visibleCount = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                var desired = child.ComputeDesiredSize(availableSize);
                maxW = Math.Max(maxW, desired.X);
                totalH += desired.Y;
                visibleCount++;
            }
            if (visibleCount > 0)
                totalH += Spacing * (visibleCount - 1);
            maxW += Padding.Left + Padding.Right;
            totalH += Padding.Top + Padding.Bottom;
            return new Vector2(maxW, totalH);
        }

        public override void ArrangeChildren()
        {
            float availableW = Size.X - Padding.Left - Padding.Right;
            float availableH = Size.Y - Padding.Top - Padding.Bottom;

            float y = CachedRect.Y + Padding.Top;
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;

                var ds = child.ComputeDesiredSize(new Vector2(availableW, availableH));
                float childW = Math.Clamp(ds.X, child.MinSize.X, child.MaxSize.X);
                float childH = Math.Clamp(ds.Y, child.MinSize.Y, child.MaxSize.Y);

                float childX = CachedRect.X + Padding.Left;
                if (Alignment == ChildAlignment.Center)
                    childX += (availableW - childW) * 0.5f;
                else if (Alignment == ChildAlignment.Right)
                    childX += availableW - childW;

                child.Arrange(new Rectangle(childX, y, childW, childH));
                y += childH + Spacing;
            }
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Overlay - 叠层面板
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 叠层面板 — 所有子元素叠放在相同 ZOrder 层，拉伸至父容器尺寸。
    /// 相当于 UE Overlay / Unity Overlay。
    /// </summary>
    public class Overlay : WidgetBase
    {
        public Overlay()
        {
            Name = "Overlay";
        }

        public override void ArrangeChildren()
        {
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;

                // 默认所有子元素拉伸到父容器尺寸
                var ds = child.ComputeDesiredSize(Size);
                float childW = ds.X;
                float childH = ds.Y;

                // 仅当锚点重合（非拉伸）时使用 DesiredSize
                if (child.AnchorMin.X == child.AnchorMax.X)
                    childW = ds.X;
                else
                    childW = Size.X - child.OffsetLeft - child.OffsetRight;

                if (child.AnchorMin.Y == child.AnchorMax.Y)
                    childH = ds.Y;
                else
                    childH = Size.Y - child.OffsetTop - child.OffsetBottom;

                childW = Math.Clamp(childW, child.MinSize.X, child.MaxSize.X);
                childH = Math.Clamp(childH, child.MinSize.Y, child.MaxSize.Y);

                float childX = CachedRect.X + child.OffsetLeft +
                    (child.AnchorMin.X * Size.X) +
                    child.Alignment.Horizontal * (Size.X - childW) * (child.AnchorMin.X == child.AnchorMax.X ? 1 : 0);

                float childY = CachedRect.Y + child.OffsetTop +
                    (child.AnchorMin.Y * Size.Y) +
                    child.Alignment.Vertical * (Size.Y - childH) * (child.AnchorMin.Y == child.AnchorMax.Y ? 1 : 0);

                child.Arrange(new Rectangle(childX, childY, childW, childH));
            }
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // GridPanel - 网格面板
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网格面板 — 按行列排列子元素。
    /// 相当于 UE GridPanel / Unity Grid Layout Group。
    /// </summary>
    public class GridPanel : WidgetBase
    {
        /// <summary>行定义：固定像素 / 比例 / Auto。</summary>
        public struct RowDefinition
        {
            public enum UnitType { Pixel, Star, Auto }
            public UnitType Unit;
            public float Value; // 像素值或比例权重

            public RowDefinition(UnitType unit, float value = 0)
            {
                Unit = unit;
                Value = value;
            }

            public static RowDefinition Fixed(float pixels) => new RowDefinition(UnitType.Pixel, pixels);
            public static RowDefinition Star(float weight = 1f) => new RowDefinition(UnitType.Star, weight);
            public static RowDefinition Auto() => new RowDefinition(UnitType.Auto);
        }

        /// <summary>列定义。</summary>
        public struct ColumnDefinition
        {
            public enum UnitType { Pixel, Star, Auto }
            public UnitType Unit;
            public float Value;

            public ColumnDefinition(UnitType unit, float value = 0)
            {
                Unit = unit;
                Value = value;
            }

            public static ColumnDefinition Fixed(float pixels) => new ColumnDefinition(UnitType.Pixel, pixels);
            public static ColumnDefinition Star(float weight = 1f) => new ColumnDefinition(UnitType.Star, weight);
            public static ColumnDefinition Auto() => new ColumnDefinition(UnitType.Auto);
        }

        public List<RowDefinition> Rows { get; set; } = new();
        public List<ColumnDefinition> Columns { get; set; } = new();
        public float RowSpacing { get; set; } = 4f;
        public float ColumnSpacing { get; set; } = 4f;

        // 子 Widget 位置映射（row, col）
        private readonly Dictionary<WidgetBase, (int row, int col)> _positions = new();

        public GridPanel()
        {
            Name = "GridPanel";
        }

        /// <summary>添加子 Widget 并指定行列位置。</summary>
        public void AddChild(WidgetBase child, int row, int col)
        {
            base.AddChild(child);
            _positions[child] = (row, col);
        }

        /// <summary>获取子 Widget 的行列位置。</summary>
        public (int row, int col) GetChildPosition(WidgetBase child)
        {
            return _positions.TryGetValue(child, out var pos) ? pos : (0, 0);
        }

        public override void ArrangeChildren()
        {
            if (Columns.Count == 0 || Rows.Count == 0) return;

            float availableW = Size.X - Padding.Left - Padding.Right;
            float availableH = Size.Y - Padding.Top - Padding.Bottom;

            // 计算列宽
            float[] colWidths = new float[Columns.Count];
            float totalStarW = 0;
            float fixedW = 0;
            int starColCount = 0;

            for (int i = 0; i < Columns.Count; i++)
            {
                var col = Columns[i];
                if (col.Unit == ColumnDefinition.UnitType.Pixel)
                {
                    colWidths[i] = col.Value;
                    fixedW += col.Value;
                }
                else if (col.Unit == ColumnDefinition.UnitType.Star)
                {
                    totalStarW += col.Value;
                    starColCount++;
                }
                else // Auto
                {
                    float maxW = 0;
                    foreach (var kvp in _positions)
                    {
                        if (kvp.Value.col == i)
                        {
                            var ds = kvp.Key.ComputeDesiredSize(new Vector2(availableW, availableH));
                            maxW = Math.Max(maxW, ds.X);
                        }
                    }
                    colWidths[i] = maxW;
                    fixedW += maxW;
                }
            }

            float remainingW = availableW - fixedW - ColumnSpacing * Math.Max(0, Columns.Count - 1);
            if (totalStarW > 0 && starColCount > 0)
            {
                for (int i = 0; i < Columns.Count; i++)
                {
                    if (Columns[i].Unit == ColumnDefinition.UnitType.Star)
                        colWidths[i] = remainingW * (Columns[i].Value / totalStarW);
                }
            }

            // 计算行高
            float[] rowHeights = new float[Rows.Count];
            float totalStarH = 0;
            float fixedH = 0;
            int starRowCount = 0;

            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if (row.Unit == RowDefinition.UnitType.Pixel)
                {
                    rowHeights[i] = row.Value;
                    fixedH += row.Value;
                }
                else if (row.Unit == RowDefinition.UnitType.Star)
                {
                    totalStarH += row.Value;
                    starRowCount++;
                }
                else // Auto
                {
                    float maxH = 0;
                    foreach (var kvp in _positions)
                    {
                        if (kvp.Value.row == i)
                        {
                            var ds = kvp.Key.ComputeDesiredSize(new Vector2(colWidths[kvp.Value.col], availableH));
                            maxH = Math.Max(maxH, ds.Y);
                        }
                    }
                    rowHeights[i] = maxH;
                    fixedH += maxH;
                }
            }

            float remainingH = availableH - fixedH - RowSpacing * Math.Max(0, Rows.Count - 1);
            if (totalStarH > 0 && starRowCount > 0)
            {
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].Unit == RowDefinition.UnitType.Star)
                        rowHeights[i] = remainingH * (Rows[i].Value / totalStarH);
                }
            }

            // 布置子 Widget
            float originX = CachedRect.X + Padding.Left;
            float originY = CachedRect.Y + Padding.Top;

            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                if (!_positions.TryGetValue(child, out var pos)) continue;
                if (pos.row >= Rows.Count || pos.col >= Columns.Count) continue;

                // 计算单元格位置
                float cellX = originX;
                float cellY = originY;
                for (int c = 0; c < pos.col; c++)
                    cellX += colWidths[c] + ColumnSpacing;
                for (int r = 0; r < pos.row; r++)
                    cellY += rowHeights[r] + RowSpacing;

                float cellW = colWidths[pos.col];
                float cellH = rowHeights[pos.row];

                child.Arrange(new Rectangle(cellX, cellY, cellW, cellH));
            }
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ScrollBox - 滚动盒子
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 滚动盒子 — 内容超出可视区时可滚动。
    /// 相当于 UE ScrollBox / Unity ScrollRect。
    /// </summary>
    public class ScrollBox : WidgetBase
    {
        public enum ScrollDirection { Vertical, Horizontal, Both }

        public ScrollDirection Direction { get; set; } = ScrollDirection.Vertical;
        public float ScrollPosition { get; set; }
        public float ScrollOffsetX { get; set; }
        public float ScrollOffsetY { get; set; }

        // 内容总尺寸
        public float ContentWidth { get; private set; }
        public float ContentHeight { get; private set; }

        // 滚动条
        public bool ShowScrollbar { get; set; } = true;
        public float ScrollbarWidth { get; set; } = 8f;
        public Color ScrollbarColor { get; set; } = new Color(0.4f, 0.4f, 0.45f, 0.7f);
        public Color ScrollbarTrackColor { get; set; } = new Color(0.2f, 0.2f, 0.22f, 0.3f);

        // 回弹效果
        public bool BounceEffect { get; set; } = true;
        public float BounceStiffness { get; set; } = 8f;

        // 惯性
        public bool Inertia { get; set; } = true;
        public float InertiaDeceleration { get; set; } = 0.95f;
        private float _velocityX;
        private float _velocityY;

        // 自动滚动到底部
        public bool AutoScrollToBottom { get; set; }

        private bool _isDraggingBar;
        private bool _isDraggingContent;
        private Vector2 _lastMousePos;

        public ScrollBox()
        {
            Name = "ScrollBox";
            Clipping = ClippingMode.ClipToBounds;
        }

        /// <summary>滚动到顶部。</summary>
        public void ScrollToTop()
        {
            ScrollOffsetY = 0;
        }

        /// <summary>滚动到底部。</summary>
        public void ScrollToBottom()
        {
            ScrollOffsetY = Math.Max(0, ContentHeight - ViewHeight);
        }

        /// <summary>滚动到左侧。</summary>
        public void ScrollToLeft()
        {
            ScrollOffsetX = 0;
        }

        /// <summary>滚动到右侧。</summary>
        public void ScrollToRight()
        {
            ScrollOffsetX = Math.Max(0, ContentWidth - ViewWidth);
        }

        private float ViewWidth => Size.X - (Direction == ScrollDirection.Vertical ? ScrollbarWidth : 0);
        private float ViewHeight => Size.Y - (Direction == ScrollDirection.Horizontal ? ScrollbarWidth : 0);

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            float maxW = 0, totalH = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                var ds = child.ComputeDesiredSize(availableSize);
                maxW = Math.Max(maxW, ds.X);
                totalH += ds.Y;
            }
            return new Vector2(
                Direction == ScrollDirection.Horizontal ? Math.Min(maxW, availableSize.X) : availableSize.X,
                Direction == ScrollDirection.Vertical ? Math.Min(totalH, availableSize.Y) : availableSize.Y);
        }

        public override void ArrangeChildren()
        {
            float vw = ViewWidth;
            float y = CachedRect.Y - ScrollOffsetY;
            float x = CachedRect.X - ScrollOffsetX;
            ContentWidth = 0;
            ContentHeight = 0;

            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;

                var ds = child.ComputeDesiredSize(new Vector2(vw, float.MaxValue));
                float childW = Direction == ScrollDirection.Vertical ? vw : ds.X;
                float childH = ds.Y;

                child.Arrange(new Rectangle(x, y, childW, childH));

                if (Direction == ScrollDirection.Vertical)
                {
                    y += childH;
                    ContentHeight += childH;
                    ContentWidth = Math.Max(ContentWidth, childW);
                }
                else if (Direction == ScrollDirection.Horizontal)
                {
                    x += childW;
                    ContentWidth += childW;
                    ContentHeight = Math.Max(ContentHeight, childH);
                }
                else
                {
                    y += childH;
                    ContentHeight += childH;
                    ContentWidth = Math.Max(ContentWidth, childW);
                }
            }

            if (AutoScrollToBottom)
                ScrollToBottom();

            ClampScroll();
        }

        private void ClampScroll()
        {
            float maxScrollX = Math.Max(0, ContentWidth - ViewWidth);
            float maxScrollY = Math.Max(0, ContentHeight - ViewHeight);

            if (BounceEffect)
            {
                // 回弹效果：软限制
                if (ScrollOffsetX < 0) ScrollOffsetX *= 0.5f;
                if (ScrollOffsetX > maxScrollX) ScrollOffsetX = maxScrollX + (ScrollOffsetX - maxScrollX) * 0.5f;
                if (ScrollOffsetY < 0) ScrollOffsetY *= 0.5f;
                if (ScrollOffsetY > maxScrollY) ScrollOffsetY = maxScrollY + (ScrollOffsetY - maxScrollY) * 0.5f;
            }
            else
            {
                ScrollOffsetX = Math.Clamp(ScrollOffsetX, 0, maxScrollX);
                ScrollOffsetY = Math.Clamp(ScrollOffsetY, 0, maxScrollY);
            }
        }

        protected internal override void OnScroll(float delta)
        {
            if (Direction == ScrollDirection.Vertical)
            {
                _velocityY += delta * 40f;
                ScrollOffsetY -= delta * 40f;
            }
            else
            {
                _velocityX += delta * 40f;
                ScrollOffsetX -= delta * 40f;
            }
            _isDraggingContent = true;
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            if (button != MouseButton.Left) return;
            _lastMousePos = localPos;
            _velocityX = 0;
            _velocityY = 0;

            // 检查是否点击了滚动条
            if (ShowScrollbar && IsScrollbarHit(localPos))
            {
                _isDraggingBar = true;
            }
            else
            {
                _isDraggingContent = true;
            }
        }

        protected internal override void OnMouseMove(Vector2 localPos)
        {
            if (_isDraggingBar)
            {
                float maxScroll = Math.Max(0, ContentHeight - ViewHeight);
                if (maxScroll > 0)
                {
                    float barHeight = ViewHeight * (ViewHeight / Math.Max(ContentHeight, 1));
                    float trackHeight = ViewHeight - barHeight;
                    float t = (localPos.Y - ScrollbarWidth * 0.5f) / Math.Max(trackHeight, 1);
                    ScrollOffsetY = Math.Clamp(t * maxScroll, 0, maxScroll);
                }
            }
            else if (_isDraggingContent)
            {
                Vector2 delta = localPos - _lastMousePos;
                ScrollOffsetX -= delta.X;
                ScrollOffsetY -= delta.Y;
                _velocityX = -delta.X;
                _velocityY = -delta.Y;
                _lastMousePos = localPos;
                ClampScroll();
            }
        }

        protected internal override void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            _isDraggingBar = false;
            _isDraggingContent = false;
        }

        private bool IsScrollbarHit(Vector2 localPos)
        {
            if (Direction == ScrollDirection.Vertical)
            {
                float barX = Size.X - ScrollbarWidth;
                return localPos.X >= barX;
            }
            return false;
        }

        public override void Tick(float deltaTime)
        {
            // 惯性衰减
            if (Inertia && !_isDraggingContent && !_isDraggingBar)
            {
                if (Math.Abs(_velocityX) > 0.1f || Math.Abs(_velocityY) > 0.1f)
                {
                    ScrollOffsetX += _velocityX;
                    ScrollOffsetY += _velocityY;
                    _velocityX *= InertiaDeceleration;
                    _velocityY *= InertiaDeceleration;
                    ClampScroll();
                }
                else
                {
                    _velocityX = 0;
                    _velocityY = 0;
                }
            }

            // 回弹修正
            if (BounceEffect && !_isDraggingContent)
            {
                float maxScrollX = Math.Max(0, ContentWidth - ViewWidth);
                float maxScrollY = Math.Max(0, ContentHeight - ViewHeight);

                if (ScrollOffsetX < 0)
                {
                    ScrollOffsetX += -ScrollOffsetX * BounceStiffness * deltaTime;
                    if (ScrollOffsetX > -0.5f) ScrollOffsetX = 0;
                }
                if (ScrollOffsetX > maxScrollX)
                {
                    float overshoot = ScrollOffsetX - maxScrollX;
                    ScrollOffsetX -= overshoot * BounceStiffness * deltaTime;
                }
                if (ScrollOffsetY < 0)
                {
                    ScrollOffsetY += -ScrollOffsetY * BounceStiffness * deltaTime;
                    if (ScrollOffsetY > -0.5f) ScrollOffsetY = 0;
                }
                if (ScrollOffsetY > maxScrollY)
                {
                    float overshoot = ScrollOffsetY - maxScrollY;
                    ScrollOffsetY -= overshoot * BounceStiffness * deltaTime;
                }
            }

            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            // 裁剪到可视区域
            context.PushClip(rect);

            // 绘制子 Widget
            var sorted = new List<WidgetBase>(Children);
            sorted.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
            foreach (var child in sorted)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                child.OnPaint(context, child.CachedRect);
            }

            context.PopClip();

            // 绘制滚动条
            if (ShowScrollbar)
            {
                DrawScrollbar(context, rect);
            }
        }

        private void DrawScrollbar(PaintContext context, Rectangle rect)
        {
            float maxScrollY = Math.Max(0, ContentHeight - ViewHeight);
            if (maxScrollY <= 0) return;

            float trackX = rect.Right - ScrollbarWidth;
            float barHeight = Math.Max(20, rect.Height * (rect.Height / Math.Max(ContentHeight, 1)));
            float barY = rect.Y + (ScrollOffsetY / maxScrollY) * (rect.Height - barHeight);

            // 轨道
            context.Renderer.FillRect(new Vector2(trackX, rect.Y),
                new Vector2(ScrollbarWidth, rect.Height), ScrollbarTrackColor);

            // 滑块
            context.Renderer.FillRect(new Vector2(trackX, barY),
                new Vector2(ScrollbarWidth, barHeight), ScrollbarColor);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // SizeBox - 尺寸盒子
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 尺寸盒子 — 限制子 Widget 的最大/最小宽高。
    /// 相当于 UE SizeBox / Unity Aspect Ratio Fitter。
    /// </summary>
    public class SizeBox : WidgetBase
    {
        public float? OverrideWidth { get; set; }
        public float? OverrideHeight { get; set; }
        public float? MaxDesiredWidth { get; set; }
        public float? MaxDesiredHeight { get; set; }
        public float? MinDesiredWidth { get; set; }
        public float? MinDesiredHeight { get; set; }

        public SizeBox()
        {
            Name = "SizeBox";
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            if (Children.Count == 0)
                return Vector2.Zero;

            var child = Children[0];
            var ds = child.ComputeDesiredSize(availableSize);

            if (OverrideWidth.HasValue) ds.X = OverrideWidth.Value;
            if (OverrideHeight.HasValue) ds.Y = OverrideHeight.Value;
            if (MaxDesiredWidth.HasValue) ds.X = Math.Min(ds.X, MaxDesiredWidth.Value);
            if (MaxDesiredHeight.HasValue) ds.Y = Math.Min(ds.Y, MaxDesiredHeight.Value);
            if (MinDesiredWidth.HasValue) ds.X = Math.Max(ds.X, MinDesiredWidth.Value);
            if (MinDesiredHeight.HasValue) ds.Y = Math.Max(ds.Y, MinDesiredHeight.Value);

            return ds;
        }

        public override void ArrangeChildren()
        {
            if (Children.Count == 0) return;
            var child = Children[0];
            if (child.Visibility == EVisibility.Collapsed) return;

            float w = OverrideWidth ?? Size.X;
            float h = OverrideHeight ?? Size.Y;
            if (MaxDesiredWidth.HasValue) w = Math.Min(w, MaxDesiredWidth.Value);
            if (MaxDesiredHeight.HasValue) h = Math.Min(h, MaxDesiredHeight.Value);
            if (MinDesiredWidth.HasValue) w = Math.Max(w, MinDesiredWidth.Value);
            if (MinDesiredHeight.HasValue) h = Math.Max(h, MinDesiredHeight.Value);

            w = Math.Clamp(w, MinSize.X, MaxSize.X);
            h = Math.Clamp(h, MinSize.Y, MaxSize.Y);

            // 居中子 Widget
            float childX = CachedRect.X + (Size.X - w) * 0.5f;
            float childY = CachedRect.Y + (Size.Y - h) * 0.5f;

            child.Arrange(new Rectangle(childX, childY, w, h));
        }

        public override void Tick(float deltaTime)
        {
            if (LayoutDirty) ArrangeChildren();
            foreach (var child in Children)
                child.Tick(deltaTime);
        }
    }
}
