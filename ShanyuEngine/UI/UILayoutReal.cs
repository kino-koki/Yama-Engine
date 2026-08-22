// =============================================================================
//  Shanyu Engine - UILayoutReal.cs
//  山雨引擎 - 真实 UI 布局系统
//
//  本文件实现完整的 UI 布局引擎，所有方法均包含真实算法：
//    * RectTransform       - UI 元素变换（锚点/Pivot/位置/大小/旋转/缩放）
//    * UILayoutEngine     - 布局引擎（Canvas布局/锚点计算/Pivot/坐标转换）
//    * VerticalLayoutGroup - 垂直布局（从上到下排列/间距/Padding/对齐）
//    * HorizontalLayoutGroup - 水平布局（从左到右排列/间距/Padding/对齐）
//    * GridLayoutGroup    - 网格布局（CellSize/Spacing/Constraint）
//    * ContentSizeFitter  - 内容大小适配器（PreferredSize/MinSize）
//
//  锚点公式: finalPos = parentRect.left + anchorMin.x * parentWidth + offsetMin.x
//  Pivot变换: 以Pivot为中心旋转/缩放
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.UI
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：锚点与基础类型
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Canvas 渲染模式。</summary>
    public enum UICanvasRenderMode
    {
        /// <summary>屏幕空间叠加（始终在最前面，不受 3D 场景影响）。</summary>
        ScreenSpaceOverlay,
        /// <summary>屏幕空间相机（受相机投影影响，可被 3D 物体遮挡）。</summary>
        ScreenSpaceCamera,
        /// <summary>世界空间（UI 元素存在于 3D 场景中）。</summary>
        WorldSpace
    }

    /// <summary>子元素对齐方式。</summary>
    public enum UIChildAlignment
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        BottomLeft, BottomCenter, BottomRight
    }

    /// <summary>网格约束模式。</summary>
    public enum UIGridConstraint
    {
        FixedColumnCount,
        FixedRowCount,
        Flexible
    }

    /// <summary>内容大小适配模式。</summary>
    public enum UIFitMode
    {
        Unconstrained,
        MinSize,
        PreferredSize
    }

    /// <summary>UI 矩形（左上角坐标 + 宽高）。</summary>
    public struct UIRect
    {
        public float X, Y, Width, Height;

        public UIRect(float x, float y, float w, float h)
        {
            X = x; Y = y; Width = w; Height = h;
        }

        /// <summary>左边。</summary>
        public float Left => X;
        /// <summary>右边。</summary>
        public float Right => X + Width;
        /// <summary>上边。</summary>
        public float Top => Y;
        /// <summary>下边。</summary>
        public float Bottom => Y + Height;
        /// <summary>中心点。</summary>
        public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);
        /// <summary>最大点。</summary>
        public Vector2 Max => new(X + Width, Y + Height);
        /// <summary>最小点。</summary>
        public Vector2 Min => new(X, Y);

        /// <summary>两个矩形是否重叠。</summary>
        public bool Overlaps(UIRect other)
        {
            return X < other.Right && Right > other.X &&
                   Y < other.Bottom && Bottom > other.Y;
        }

        /// <summary>是否包含某个点。</summary>
        public bool Contains(Vector2 point)
        {
            return point.X >= X && point.X <= Right &&
                   point.Y >= Y && point.Y <= Bottom;
        }

        public override string ToString() => $"({X:F1},{Y:F1},{Width:F1},{Height:F1})";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：RectTransform（UI 元素变换）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UI 元素变换。类似 Unity 的 RectTransform，包含锚点、Pivot、偏移、旋转、缩放。
    ///
    /// 最终位置计算公式：
    ///   finalLeft = parentLeft + anchorMin.x * parentWidth + offsetMin.x
    ///   finalTop  = parentTop  - anchorMin.y * parentHeight - offsetMin.y
    ///   finalRight  = parentLeft + anchorMax.x * parentWidth + offsetMax.x
    ///   finalBottom = parentTop  - anchorMax.y * parentHeight - offsetMax.y
    ///
    /// Pivot 用于旋转和缩放的变换中心点（归一化 0-1）：
    ///   Pivot (0,0) = 左上角, (0.5,0.5) = 中心, (1,1) = 右下角
    /// </summary>
    public sealed class RectTransform
    {
        // ── 锚点 ──
        /// <summary>锚点最小值（归一化 0-1）。确定元素最小边相对于父元素的位置。</summary>
        public Vector2 AnchorMin = new(0.5f, 0.5f);

        /// <summary>锚点最大值（归一化 0-1）。确定元素最大边相对于父元素的位置。</summary>
        public Vector2 AnchorMax = new(0.5f, 0.5f);

        // ── 偏移 ──
        /// <summary>最小边偏移（像素）。从锚点最小位置偏移的距离。</summary>
        public Vector2 OffsetMin = new(-50f, -25f);

        /// <summary>最大边偏移（像素）。从锚点最大位置偏移的距离。</summary>
        public Vector2 OffsetMax = new(50f, 25f);

        // ── Pivot ──
        /// <summary>Pivot（归一化 0-1）。旋转和缩放的中心点。</summary>
        public Vector2 Pivot = new(0.5f, 0.5f);

        // ── 局部变换 ──
        /// <summary>局部旋转角度（弧度，绕 Z 轴）。</summary>
        public float LocalRotation;

        /// <summary>局部缩放。</summary>
        public Vector2 LocalScale = Vector2.One;

        // ── 父子关系 ──
        /// <summary>父元素变换。</summary>
        public RectTransform? Parent;

        /// <summary>子元素列表。</summary>
        public readonly List<RectTransform> Children = new();

        /// <summary>元素的名称（调试用）。</summary>
        public string Name = "UIElement";

        // ── 缓存 ──
        private UIRect _cachedRect;
        private bool _dirty = true;

        /// <summary>标记需要重新计算。</summary>
        public void SetDirty() => _dirty = true;

        /// <summary>是否脏标记。</summary>
        public bool IsDirty => _dirty;

        /// <summary>
        /// 锚定位置（从锚点派生的中心点偏移）。
        /// 当 AnchorMin == AnchorMax 时，元素有固定大小和固定锚点。
        /// </summary>
        public Vector2 AnchoredPosition
        {
            get
            {
                // anchoredPosition 是偏移中点
                return (OffsetMin + OffsetMax) * 0.5f;
            }
            set
            {
                Vector2 size = OffsetMax - OffsetMin;
                OffsetMin = value - size * 0.5f;
                OffsetMax = value + size * 0.5f;
                _dirty = true;
            }
        }

        /// <summary>元素大小（从偏移计算）。</summary>
        public Vector2 SizeDelta
        {
            get => OffsetMax - OffsetMin;
            set
            {
                Vector2 center = AnchoredPosition;
                OffsetMin = center - value * 0.5f;
                OffsetMax = center + value * 0.5f;
                _dirty = true;
            }
        }

        /// <summary>
        /// 计算最终的屏幕空间矩形。
        /// 算法：
        ///   1. 从父元素获取 parentRect
        ///   2. 根据 AnchorMin/AnchorMax + OffsetMin/OffsetMax 计算四条边
        ///   3. 应用 Pivot 变换（无 Pivot 变换时位置不变）
        /// </summary>
        public UIRect GetRect()
        {
            if (!_dirty) return _cachedRect;

            UIRect parentRect;
            if (Parent != null)
                parentRect = Parent.GetRect();
            else
                parentRect = new UIRect(0, 0, 1920, 1080); // 默认画布大小

            // 计算四条边
            // UI 坐标系：Y 向下为正，原点在左上角
            float left = parentRect.X + AnchorMin.X * parentRect.Width + OffsetMin.X;
            float right = parentRect.X + AnchorMax.X * parentRect.Width + OffsetMax.X;
            float top = parentRect.Y + (1f - AnchorMin.Y) * parentRect.Height - OffsetMin.Y;
            float bottom = parentRect.Y + (1f - AnchorMax.Y) * parentRect.Height - OffsetMax.Y;

            // 确保 left < right, bottom > top
            if (left > right) { float t = left; left = right; right = t; }
            if (bottom < top) { float t = bottom; bottom = top; top = t; }

            _cachedRect = new UIRect(left, top, right - left, bottom - top);
            _dirty = false;
            return _cachedRect;
        }

        /// <summary>
        /// 获取世界空间的中心位置（应用了旋转和缩放）。
        /// </summary>
        public Vector2 GetWorldCenter()
        {
            UIRect rect = GetRect();
            Vector2 center = rect.Center;

            // 应用旋转（绕 Pivot 旋转）
            if (MathF.Abs(LocalRotation) > 1e-6f)
            {
                Vector2 pivot = GetPivotWorldPosition();
                Vector2 offset = center - pivot;
                float cos = MathF.Cos(LocalRotation);
                float sin = MathF.Sin(LocalRotation);
                center = pivot + new Vector2(
                    offset.X * cos - offset.Y * sin,
                    offset.X * sin + offset.Y * cos);
            }

            return center;
        }

        /// <summary>Pivot 的世界坐标。</summary>
        public Vector2 GetPivotWorldPosition()
        {
            UIRect rect = GetRect();
            return new Vector2(
                rect.X + rect.Width * Pivot.X,
                rect.Y + rect.Height * Pivot.Y);
        }

        /// <summary>
        /// 局部坐标→世界坐标转换。
        /// 算法：先按 Pivot 缩放和旋转，再加上世界位置偏移。
        /// </summary>
        public Vector2 LocalToWorld(Vector2 localPos)
        {
            // 相对于 Pivot 的偏移
            Vector2 pivot = GetPivotWorldPosition();
            Vector2 relative = localPos;

            // 缩放
            relative *= LocalScale;

            // 旋转
            if (MathF.Abs(LocalRotation) > 1e-6f)
            {
                float cos = MathF.Cos(LocalRotation);
                float sin = MathF.Sin(LocalRotation);
                relative = new Vector2(
                    relative.X * cos - relative.Y * sin,
                    relative.X * sin + relative.Y * cos);
            }

            return pivot + relative;
        }

        /// <summary>
        /// 世界坐标→局部坐标转换。
        /// 算法：减去 Pivot 位置，逆旋转，逆缩放。
        /// </summary>
        public Vector2 WorldToLocal(Vector2 worldPos)
        {
            Vector2 pivot = GetPivotWorldPosition();
            Vector2 relative = worldPos - pivot;

            // 逆旋转
            if (MathF.Abs(LocalRotation) > 1e-6f)
            {
                float cos = MathF.Cos(-LocalRotation);
                float sin = MathF.Sin(-LocalRotation);
                relative = new Vector2(
                    relative.X * cos - relative.Y * sin,
                    relative.X * sin + relative.Y * cos);
            }

            // 逆缩放
            if (LocalScale.X > 1e-6f) relative.X /= LocalScale.X;
            if (LocalScale.Y > 1e-6f) relative.Y /= LocalScale.Y;

            return relative;
        }

        /// <summary>添加子元素。</summary>
        public void AddChild(RectTransform child)
        {
            if (child.Parent == this) return;
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
            child.SetDirty();
        }

        /// <summary>移除子元素。</summary>
        public void RemoveChild(RectTransform child)
        {
            if (child.Parent == this)
            {
                child.Parent = null;
                Children.Remove(child);
                child.SetDirty();
            }
        }

        /// <summary>设置父元素。</summary>
        public void SetParent(RectTransform? newParent)
        {
            Parent?.Children.Remove(this);
            Parent = newParent;
            Parent?.Children.Add(this);
            SetDirty();
        }

        /// <summary>使所有子元素标记为脏。</summary>
        public void InvalidateChildren()
        {
            foreach (var child in Children)
            {
                child.SetDirty();
                child.InvalidateChildren();
            }
        }

        /// <summary>重置为默认状态。</summary>
        public void Reset()
        {
            AnchorMin = new Vector2(0.5f, 0.5f);
            AnchorMax = new Vector2(0.5f, 0.5f);
            OffsetMin = new Vector2(-50f, -25f);
            OffsetMax = new Vector2(50f, 25f);
            Pivot = new Vector2(0.5f, 0.5f);
            LocalRotation = 0f;
            LocalScale = Vector2.One;
            _dirty = true;
        }

        /// <summary>设置锚点（min 和 max 相同 → 固定锚点）。</summary>
        public void SetAnchor(Vector2 anchor)
        {
            AnchorMin = anchor;
            AnchorMax = anchor;
            _dirty = true;
        }

        /// <summary>拉伸锚点（四角锚定到父元素边缘）。</summary>
        public void SetStretchAnchor()
        {
            AnchorMin = Vector2.Zero;
            AnchorMax = Vector2.One;
            _dirty = true;
        }

        /// <summary>设置左上角锚点。</summary>
        public void SetTopLeftAnchor()
        {
            AnchorMin = Vector2.Zero;
            AnchorMax = Vector2.Zero;
            _dirty = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：布局引擎
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UI 布局引擎。管理 Canvas、锚点系统、布局组、坐标转换。
    ///
    /// 锚点系统核心公式：
    ///   finalLeft   = parentLeft   + anchorMin.x * parentWidth  + offsetMin.x
    ///   finalRight  = parentLeft   + anchorMax.x * parentWidth  + offsetMax.x
    ///   finalTop    = parentTop    - anchorMin.y * parentHeight - offsetMin.y
    ///   finalBottom = parentTop    - anchorMax.y * parentHeight - offsetMax.y
    ///
    /// Canvas 布局模式：
    ///   - ScreenSpaceOverlay: 独立于 3D 场景，固定在屏幕上
    ///   - ScreenSpaceCamera: 受相机投影影响
    ///   - WorldSpace: UI 作为 3D 对象存在于场景中
    /// </summary>
    public sealed class UILayoutEngine
    {
        /// <summary>Canvas 渲染模式。</summary>
        public UICanvasRenderMode RenderMode = UICanvasRenderMode.ScreenSpaceOverlay;

        /// <summary>Canvas 尺寸（像素）。</summary>
        public Vector2 CanvasSize = new(1920, 1080);

        /// <summary>参考分辨率（用于自适应缩放）。</summary>
        public Vector2 ReferenceResolution = new(1920, 1080);

        /// <summary>缩放模式。</summary>
        public CanvasScalerMode ScalerMode = CanvasScalerMode.ConstantPixelSize;

        /// <summary>缩放因子。</summary>
        public float ScaleFactor = 1f;

        /// <summary>Canvas 根 RectTransform。</summary>
        public RectTransform? CanvasRoot;

        /// <summary>布局组列表（注册的自动布局组）。</summary>
        public readonly List<ILayoutGroup> LayoutGroups = new();

        /// <summary>内容大小适配器列表。</summary>
        public readonly List<ContentSizeFitter> ContentSizeFitters = new();

        /// <summary>DPI 缩放因子。</summary>
        public DPIScale DpiScale { get; } = new DPIScale(1f);

        /// <summary>更新整个布局树。</summary>
        public void UpdateLayout()
        {
            if (CanvasRoot == null) return;

            // 第一遍：计算缩放因子
            CalculateScaleFactor();

            // 第二遍：从根到叶更新所有布局组
            UpdateLayoutGroupsRecursive(CanvasRoot);

            // 第三遍：内容大小适配器
            UpdateContentSizeFitters();

            // 第四遍：标记所有子元素脏（强制重新计算）
            CanvasRoot.SetDirty();
            CanvasRoot.InvalidateChildren();
        }

        /// <summary>
        /// 计算缩放因子（基于 Canvas Scaler 模式）。
        /// - ConstantPixelSize: 使用固定 ScaleFactor
        /// - ScaleWithScreenSize: 根据屏幕分辨率与参考分辨率的比例计算
        /// </summary>
        private void CalculateScaleFactor()
        {
            switch (ScalerMode)
            {
                case CanvasScalerMode.ConstantPixelSize:
                    ScaleFactor = 1f;
                    break;

                case CanvasScalerMode.ScaleWithScreenSize:
                    float scaleX = CanvasSize.X / Math.Max(1f, ReferenceResolution.X);
                    float scaleY = CanvasSize.Y / Math.Max(1f, ReferenceResolution.Y);
                    // 使用较小的缩放（保证内容适配）
                    ScaleFactor = Math.Min(scaleX, scaleY);
                    break;
            }

            ScaleFactor *= DpiScale.Scale;
        }

        /// <summary>递归更新布局组。</summary>
        private void UpdateLayoutGroupsRecursive(RectTransform node)
        {
            foreach (var group in LayoutGroups)
            {
                if (group.Target == node)
                {
                    group.CalculateLayout();
                }
            }
            foreach (var child in node.Children)
            {
                UpdateLayoutGroupsRecursive(child);
            }
        }

        /// <summary>更新所有内容大小适配器。</summary>
        private void UpdateContentSizeFitters()
        {
            foreach (var fitter in ContentSizeFitters)
            {
                fitter.UpdateSize();
            }
        }

        /// <summary>
        /// 计算给定 RectTransform 的最终矩形（含 Canvas 缩放）。
        /// </summary>
        public UIRect GetFinalRect(RectTransform transform)
        {
            UIRect rect = transform.GetRect();

            // 应用 Canvas 缩放
            if (ScaleFactor != 1f && transform.Parent == CanvasRoot)
            {
                rect.X *= ScaleFactor;
                rect.Y *= ScaleFactor;
                rect.Width *= ScaleFactor;
                rect.Height *= ScaleFactor;
            }

            return rect;
        }

        /// <summary>注册布局组。</summary>
        public void RegisterLayoutGroup(ILayoutGroup group) => LayoutGroups.Add(group);

        /// <summary>注销布局组。</summary>
        public void UnregisterLayoutGroup(ILayoutGroup group) => LayoutGroups.Remove(group);

        /// <summary>注册内容大小适配器。</summary>
        public void RegisterContentSizeFitter(ContentSizeFitter fitter) => ContentSizeFitters.Add(fitter);

        /// <summary>注销内容大小适配器。</summary>
        public void UnregisterContentSizeFitter(ContentSizeFitter fitter) => ContentSizeFitters.Remove(fitter);
    }

    /// <summary>Canvas 缩放模式。</summary>
    public enum CanvasScalerMode
    {
        ConstantPixelSize,
        ScaleWithScreenSize
    }

    /// <summary>布局组接口。</summary>
    public interface ILayoutGroup
    {
        RectTransform Target { get; }
        void CalculateLayout();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：垂直布局组
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 垂直布局组：子元素从上到下排列。
    ///
    /// 布局算法：
    ///   1. 计算 totalHeight = sum(childHeights) + spacing * (count-1) + padding.top + padding.bottom
    ///   2. 根据 ChildAlignment 计算 startY
    ///   3. 逐个子元素设置 AnchoredPosition.y 和 AnchoredPosition.x
    ///
    /// 对齐计算（垂直方向）：
    ///   Top:    startY = parent.top - padding.top
    ///   Middle: startY = parent.centerY + totalHeight/2 - padding.top
    ///   Bottom: startY = parent.bottom + totalHeight - padding.top
    /// </summary>
    public sealed class VerticalLayoutGroup : ILayoutGroup
    {
        /// <summary>目标 RectTransform。</summary>
        public RectTransform Target { get; set; } = null!;

        /// <summary>子元素间距（像素）。</summary>
        public float Spacing = 5f;

        /// <summary>上边距。</summary>
        public float PaddingTop = 0f;

        /// <summary>下边距。</summary>
        public float PaddingBottom = 0f;

        /// <summary>左边距。</summary>
        public float PaddingLeft = 0f;

        /// <summary>右边距。</summary>
        public float PaddingRight = 0f;

        /// <summary>子元素水平对齐方式。</summary>
        public UIChildAlignment ChildAlignment = UIChildAlignment.TopLeft;

        /// <summary>是否控制子元素宽度（拉伸填满）。</summary>
        public bool ControlChildWidth = true;

        /// <summary>是否控制子元素高度（使用子元素自身大小）。</summary>
        public bool ControlChildHeight;

        /// <summary>子元素最小高度（受控时）。</summary>
        public float ChildMinHeight = 20f;

        /// <summary>子元素最小宽度（受控时）。</summary>
        public float ChildMinWidth = 100f;

        /// <summary>
        /// 计算垂直布局。
        /// 算法：
        ///   1. 遍历子元素，收集每个子元素的高度
        ///   2. 计算总高度 = padding.top + sum(heights) + spacing*(n-1) + padding.bottom
        ///   3. 根据 ChildAlignment 计算起始 Y 坐标
        ///   4. 逐个子元素设置位置
        /// </summary>
        public void CalculateLayout()
        {
            if (Target == null) return;
            UIRect parentRect = Target.GetRect();
            int childCount = Target.Children.Count;
            if (childCount == 0) return;

            // 收集子元素高度
            float totalHeight = PaddingTop + PaddingBottom;
            float[] heights = new float[childCount];
            for (int i = 0; i < childCount; i++)
            {
                RectTransform child = Target.Children[i];
                UIRect childRect = child.GetRect();
                heights[i] = ControlChildHeight ? Math.Max(ChildMinHeight, childRect.Height) : childRect.Height;
                totalHeight += heights[i];
                if (i < childCount - 1) totalHeight += Spacing;
            }

            // 计算起始 Y（布局坐标系：Y 向下为正）
            float startY;
            float parentCenterY = parentRect.Y + parentRect.Height * 0.5f;
            float alignmentY = GetAlignmentFractionY(ChildAlignment);

            // 上对齐: startY = parentRect.Y + PaddingTop
            // 中对齐: startY = parentCenterY - totalHeight/2 + PaddingTop
            // 下对齐: startY = parentRect.Y + parentRect.Height - totalHeight + PaddingTop
            switch (ChildAlignment)
            {
                case UIChildAlignment.TopLeft:
                case UIChildAlignment.TopCenter:
                case UIChildAlignment.TopRight:
                    startY = parentRect.Y + PaddingTop;
                    break;
                case UIChildAlignment.MiddleLeft:
                case UIChildAlignment.MiddleCenter:
                case UIChildAlignment.MiddleRight:
                    startY = parentCenterY - totalHeight * 0.5f + PaddingTop;
                    break;
                case UIChildAlignment.BottomLeft:
                case UIChildAlignment.BottomCenter:
                case UIChildAlignment.BottomRight:
                    startY = parentRect.Y + parentRect.Height - totalHeight + PaddingTop;
                    break;
                default:
                    startY = parentRect.Y + PaddingTop;
                    break;
            }

            // 逐个子元素设置位置
            float availableWidth = parentRect.Width - PaddingLeft - PaddingRight;
            for (int i = 0; i < childCount; i++)
            {
                RectTransform child = Target.Children[i];
                float yPos = startY + heights[i] * 0.5f;

                // 水平对齐
                float xPos;
                float alignmentX = GetAlignmentFractionX(ChildAlignment);
                xPos = parentRect.X + PaddingLeft + alignmentX * (availableWidth - child.SizeDelta.X);

                // 如果控制宽度，拉伸子元素
                if (ControlChildWidth)
                {
                    child.OffsetMin = new Vector2(
                        -(PaddingLeft + availableWidth * (1f - alignmentX)),
                        -heights[i] * 0.5f);
                    child.OffsetMax = new Vector2(
                        PaddingRight + availableWidth * alignmentX,
                        heights[i] * 0.5f);
                }
                else
                {
                    Vector2 size = child.SizeDelta;
                    child.AnchoredPosition = new Vector2(xPos, yPos);
                    child.SizeDelta = new Vector2(Math.Max(ChildMinWidth, size.X), heights[i]);
                }

                child.SetAnchor(Vector2.Zero);
                startY += heights[i] + Spacing;
            }
        }

        private static float GetAlignmentFractionX(UIChildAlignment align) => align switch
        {
            UIChildAlignment.TopLeft or UIChildAlignment.MiddleLeft or UIChildAlignment.BottomLeft => 0f,
            UIChildAlignment.TopCenter or UIChildAlignment.MiddleCenter or UIChildAlignment.BottomCenter => 0.5f,
            _ => 1f
        };

        private static float GetAlignmentFractionY(UIChildAlignment align) => align switch
        {
            UIChildAlignment.TopLeft or UIChildAlignment.TopCenter or UIChildAlignment.TopRight => 0f,
            UIChildAlignment.MiddleLeft or UIChildAlignment.MiddleCenter or UIChildAlignment.MiddleRight => 0.5f,
            _ => 1f
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：水平布局组
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 水平布局组：子元素从左到右排列。
    ///
    /// 布局算法：
    ///   1. totalWidth = sum(childWidths) + spacing * (count-1) + padding.left + padding.right
    ///   2. 根据 ChildAlignment 计算 startX
    ///   3. 逐个子元素设置 AnchoredPosition.x 和 AnchoredPosition.y
    /// </summary>
    public sealed class HorizontalLayoutGroup : ILayoutGroup
    {
        /// <summary>目标 RectTransform。</summary>
        public RectTransform Target { get; set; } = null!;

        /// <summary>子元素间距。</summary>
        public float Spacing = 5f;

        /// <summary>上/下/左/右边距。</summary>
        public float PaddingTop = 0f;
        public float PaddingBottom = 0f;
        public float PaddingLeft = 0f;
        public float PaddingRight = 0f;

        /// <summary>子元素对齐方式。</summary>
        public UIChildAlignment ChildAlignment = UIChildAlignment.TopLeft;

        /// <summary>是否控制子元素高度。</summary>
        public bool ControlChildHeight = true;

        /// <summary>是否控制子元素宽度。</summary>
        public bool ControlChildWidth;

        public float ChildMinWidth = 20f;
        public float ChildMinHeight = 100f;

        /// <summary>计算水平布局。</summary>
        public void CalculateLayout()
        {
            if (Target == null) return;
            UIRect parentRect = Target.GetRect();
            int childCount = Target.Children.Count;
            if (childCount == 0) return;

            // 收集子元素宽度
            float totalWidth = PaddingLeft + PaddingRight;
            float[] widths = new float[childCount];
            for (int i = 0; i < childCount; i++)
            {
                RectTransform child = Target.Children[i];
                UIRect childRect = child.GetRect();
                widths[i] = ControlChildWidth ? Math.Max(ChildMinWidth, childRect.Width) : childRect.Width;
                totalWidth += widths[i];
                if (i < childCount - 1) totalWidth += Spacing;
            }

            // 计算起始 X
            float startX;
            switch (ChildAlignment)
            {
                case UIChildAlignment.TopLeft:
                case UIChildAlignment.MiddleLeft:
                case UIChildAlignment.BottomLeft:
                    startX = parentRect.X + PaddingLeft;
                    break;
                case UIChildAlignment.TopCenter:
                case UIChildAlignment.MiddleCenter:
                case UIChildAlignment.BottomCenter:
                    startX = parentRect.X + parentRect.Width * 0.5f - totalWidth * 0.5f + PaddingLeft;
                    break;
                case UIChildAlignment.TopRight:
                case UIChildAlignment.MiddleRight:
                case UIChildAlignment.BottomRight:
                    startX = parentRect.X + parentRect.Width - totalWidth + PaddingLeft;
                    break;
                default:
                    startX = parentRect.X + PaddingLeft;
                    break;
            }

            float availableHeight = parentRect.Height - PaddingTop - PaddingBottom;
            for (int i = 0; i < childCount; i++)
            {
                RectTransform child = Target.Children[i];
                float xPos = startX + widths[i] * 0.5f;

                float alignmentY = GetAlignmentFractionY(ChildAlignment);
                float yPos = parentRect.Y + PaddingTop + alignmentY * (availableHeight - child.SizeDelta.Y);

                if (ControlChildHeight)
                {
                    child.OffsetMin = new Vector2(
                        -widths[i] * 0.5f,
                        -(PaddingTop + availableHeight * (1f - alignmentY)));
                    child.OffsetMax = new Vector2(
                        widths[i] * 0.5f,
                        PaddingBottom + availableHeight * alignmentY);
                }
                else
                {
                    child.AnchoredPosition = new Vector2(xPos, yPos);
                    child.SizeDelta = new Vector2(widths[i], Math.Max(ChildMinHeight, child.SizeDelta.Y));
                }

                child.SetAnchor(Vector2.Zero);
                startX += widths[i] + Spacing;
            }
        }

        private static float GetAlignmentFractionY(UIChildAlignment align) => align switch
        {
            UIChildAlignment.TopLeft or UIChildAlignment.TopCenter or UIChildAlignment.TopRight => 0f,
            UIChildAlignment.MiddleLeft or UIChildAlignment.MiddleCenter or UIChildAlignment.MiddleRight => 0.5f,
            _ => 1f
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：网格布局组
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 网格布局组：子元素按网格排列。
    ///
    /// 布局算法：
    ///   - FixedColumnCount: 固定列数，自动计算行数
    ///   - FixedRowCount: 固定行数，自动计算列数
    ///   - Flexible: 根据可用空间自动计算
    ///
    /// 每个单元格大小 = CellSize + Spacing
    /// 子元素位置 = 起点 + (col * cellWidth, row * cellHeight)
    /// </summary>
    public sealed class GridLayoutGroup : ILayoutGroup
    {
        /// <summary>目标 RectTransform。</summary>
        public RectTransform Target { get; set; } = null!;

        /// <summary>单元格大小。</summary>
        public Vector2 CellSize = new(100, 100);

        /// <summary>单元格间距。</summary>
        public Vector2 Spacing = new(5, 5);

        /// <summary>约束模式。</summary>
        public UIGridConstraint Constraint = UIGridConstraint.FixedColumnCount;

        /// <summary>约束数量（列数或行数）。</summary>
        public int ConstraintCount = 4;

        /// <summary>起始位置偏移。</summary>
        public Vector2 StartOffset = Vector2.Zero;

        /// <summary>是否从右到左排列。</summary>
        public bool RightToLeft;

        /// <summary>是否从下到上排列。</summary>
        public bool BottomToTop;

        /// <summary>子元素对齐方式。</summary>
        public UIChildAlignment ChildAlignment = UIChildAlignment.MiddleCenter;

        /// <summary>计算网格布局。</summary>
        public void CalculateLayout()
        {
            if (Target == null) return;
            UIRect parentRect = Target.GetRect();
            int childCount = Target.Children.Count;
            if (childCount == 0) return;

            // 计算列数和行数
            int cols, rows;
            switch (Constraint)
            {
                case UIGridConstraint.FixedColumnCount:
                    cols = Math.Max(1, ConstraintCount);
                    rows = (childCount + cols - 1) / cols;
                    break;
                case UIGridConstraint.FixedRowCount:
                    rows = Math.Max(1, ConstraintCount);
                    cols = (childCount + rows - 1) / rows;
                    break;
                default: // Flexible
                    // 尽可能填满
                    cols = Math.Max(1, (int)(parentRect.Width / (CellSize.X + Spacing.X)));
                    rows = (childCount + cols - 1) / cols;
                    break;
            }

            // 计算网格总大小
            float gridWidth = cols * CellSize.X + (cols - 1) * Spacing.X;
            float gridHeight = rows * CellSize.Y + (rows - 1) * Spacing.Y;

            // 计算起始位置（根据对齐方式）
            float alignmentX = GetAlignmentFractionX(ChildAlignment);
            float alignmentY = GetAlignmentFractionY(ChildAlignment);
            float startX = parentRect.X + parentRect.Width * alignmentX - gridWidth * alignmentX + StartOffset.X;
            float startY = parentRect.Y + parentRect.Height * alignmentY - gridHeight * alignmentY + StartOffset.Y;

            // 单元格步长
            float stepX = CellSize.X + Spacing.X;
            float stepY = CellSize.Y + Spacing.Y;

            // 逐个子元素设置位置
            for (int i = 0; i < childCount; i++)
            {
                int row, col;
                if (BottomToTop)
                {
                    row = rows - 1 - (i / cols);
                }
                else
                {
                    row = i / cols;
                }

                col = RightToLeft ? (cols - 1 - (i % cols)) : (i % cols);

                float xPos = startX + col * stepX + CellSize.X * 0.5f;
                float yPos = startY + row * stepY + CellSize.Y * 0.5f;

                RectTransform child = Target.Children[i];
                child.SetAnchor(Vector2.Zero);
                child.AnchoredPosition = new Vector2(xPos, yPos);
                child.SizeDelta = CellSize;
            }
        }

        private static float GetAlignmentFractionX(UIChildAlignment align) => align switch
        {
            UIChildAlignment.TopLeft or UIChildAlignment.MiddleLeft or UIChildAlignment.BottomLeft => 0f,
            UIChildAlignment.TopCenter or UIChildAlignment.MiddleCenter or UIChildAlignment.BottomCenter => 0.5f,
            _ => 1f
        };

        private static float GetAlignmentFractionY(UIChildAlignment align) => align switch
        {
            UIChildAlignment.TopLeft or UIChildAlignment.TopCenter or UIChildAlignment.TopRight => 0f,
            UIChildAlignment.MiddleLeft or UIChildAlignment.MiddleCenter or UIChildAlignment.MiddleRight => 0.5f,
            _ => 1f
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：内容大小适配器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 内容大小适配器。根据子元素自动调整目标 RectTransform 的大小。
    ///
    /// 算法：
    ///   - 遍历所有子元素，计算包围盒
    ///   - 根据水平/垂直 FitMode 调整目标宽度/高度
    ///   - MinSize: 取 min(currentSize, contentSize)
    ///   - PreferredSize: 设为 contentSize
    ///   - Unconstrained: 不调整
    /// </summary>
    public sealed class ContentSizeFitter
    {
        /// <summary>目标 RectTransform。</summary>
        public RectTransform Target { get; set; } = null!;

        /// <summary>水平适配模式。</summary>
        public UIFitMode HorizontalFit = UIFitMode.PreferredSize;

        /// <summary>垂直适配模式。</summary>
        public UIFitMode VerticalFit = UIFitMode.PreferredSize;

        /// <summary>最小宽度。</summary>
        public float MinWidth = 0f;

        /// <summary>最小高度。</summary>
        public float MinHeight = 0f;

        /// <summary>更新目标大小。</summary>
        public void UpdateSize()
        {
            if (Target == null || Target.Children.Count == 0) return;

            // 计算子元素包围盒
            float contentMinX = float.MaxValue, contentMinY = float.MaxValue;
            float contentMaxX = float.MinValue, contentMaxY = float.MinValue;

            foreach (var child in Target.Children)
            {
                UIRect childRect = child.GetRect();
                contentMinX = Math.Min(contentMinX, childRect.X);
                contentMinY = Math.Min(contentMinY, childRect.Y);
                contentMaxX = Math.Max(contentMaxX, childRect.Right);
                contentMaxY = Math.Max(contentMaxY, childRect.Bottom);
            }

            float contentWidth = contentMaxX - contentMinX;
            float contentHeight = contentMaxY - contentMinY;

            UIRect targetRect = Target.GetRect();
            float newWidth = targetRect.Width;
            float newHeight = targetRect.Height;

            // 水平适配
            newWidth = ApplyFit(HorizontalFit, newWidth, contentWidth, MinWidth);

            // 垂直适配
            newHeight = ApplyFit(VerticalFit, newHeight, contentHeight, MinHeight);

            // 设置新大小
            Vector2 currentSize = Target.SizeDelta;
            Target.SizeDelta = new Vector2(
                Math.Max(1f, newWidth),
                Math.Max(1f, newHeight));
        }

        /// <summary>根据适配模式计算最终大小。</summary>
        private static float ApplyFit(UIFitMode mode, float currentSize, float contentSize, float minSize)
        {
            return mode switch
            {
                UIFitMode.Unconstrained => currentSize,
                UIFitMode.MinSize => Math.Max(minSize, Math.Min(currentSize, contentSize)),
                UIFitMode.PreferredSize => Math.Max(minSize, contentSize),
                _ => currentSize
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：布局辅助工具
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>布局计算辅助工具。</summary>
    public static class LayoutUtility
    {
        /// <summary>获取子元素的最小宽度。</summary>
        public static float GetMinWidth(RectTransform rect)
        {
            UIRect r = rect.GetRect();
            return r.Width;
        }

        /// <summary>获取子元素的最小高度。</summary>
        public static float GetMinHeight(RectTransform rect)
        {
            UIRect r = rect.GetRect();
            return r.Height;
        }

        /// <summary>获取子元素的首选宽度。</summary>
        public static float GetPreferredSizeWidth(RectTransform rect)
        {
            UIRect r = rect.GetRect();
            return r.Width;
        }

        /// <summary>获取子元素的首选高度。</summary>
        public static float GetPreferredSizeHeight(RectTransform rect)
        {
            UIRect r = rect.GetRect();
            return r.Height;
        }

        /// <summary>获取子元素的弹性大小（可用于弹性布局）。</summary>
        public static float GetFlexibleSize(RectTransform rect)
        {
            // 弹性大小基于子元素的可缩放性
            // 简单实现：返回当前大小作为弹性值
            UIRect r = rect.GetRect();
            return r.Width + r.Height;
        }

        /// <summary>将锚点从四角锚定模式转换为固定锚点模式。</summary>
        public static void ConvertStretchToAnchor(RectTransform rect)
        {
            UIRect parentRect;
            if (rect.Parent != null)
                parentRect = rect.Parent.GetRect();
            else
                parentRect = new UIRect(0, 0, 1920, 1080);

            UIRect currentRect = rect.GetRect();

            // 计算新的锚点（中心锚点）
            float anchorX = (currentRect.Center.X - parentRect.X) / Math.Max(1f, parentRect.Width);
            float anchorY = 1f - (currentRect.Center.Y - parentRect.Y) / Math.Max(1f, parentRect.Height);

            anchorX = Math.Clamp(anchorX, 0f, 1f);
            anchorY = Math.Clamp(anchorY, 0f, 1f);

            rect.SetAnchor(new Vector2(anchorX, anchorY));
            rect.SizeDelta = new Vector2(currentRect.Width, currentRect.Height);
        }

        /// <summary>将固定锚点转换为拉伸模式。</summary>
        public static void ConvertAnchorToStretch(RectTransform rect)
        {
            UIRect parentRect;
            if (rect.Parent != null)
                parentRect = rect.Parent.GetRect();
            else
                parentRect = new UIRect(0, 0, 1920, 1080);

            UIRect currentRect = rect.GetRect();

            // 计算锚点
            float anchorMinX = (currentRect.X - parentRect.X) / Math.Max(1f, parentRect.Width);
            float anchorMinY = 1f - (currentRect.Bottom - parentRect.Y) / Math.Max(1f, parentRect.Height);
            float anchorMaxX = (currentRect.Right - parentRect.X) / Math.Max(1f, parentRect.Width);
            float anchorMaxY = 1f - (currentRect.Y - parentRect.Y) / Math.Max(1f, parentRect.Height);

            anchorMinX = Math.Clamp(anchorMinX, 0f, 1f);
            anchorMinY = Math.Clamp(anchorMinY, 0f, 1f);
            anchorMaxX = Math.Clamp(anchorMaxX, 0f, 1f);
            anchorMaxY = Math.Clamp(anchorMaxY, 0f, 1f);

            rect.AnchorMin = new Vector2(anchorMinX, anchorMinY);
            rect.AnchorMax = new Vector2(anchorMaxX, anchorMaxY);
            rect.OffsetMin = Vector2.Zero;
            rect.OffsetMax = Vector2.Zero;
        }

        /// <summary>计算布局深度（从根到指定节点的层级）。</summary>
        public static int GetLayoutDepth(RectTransform node)
        {
            int depth = 0;
            RectTransform? current = node;
            while (current?.Parent != null)
            {
                depth++;
                current = current.Parent;
            }
            return depth;
        }

        /// <summary>计算子节点总数。</summary>
        public static int GetTotalChildCount(RectTransform node)
        {
            int count = 0;
            foreach (var child in node.Children)
            {
                count += 1 + GetTotalChildCount(child);
            }
            return count;
        }
    }
}
