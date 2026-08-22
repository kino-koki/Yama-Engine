using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Input;

namespace ShanyuEngine.UI
{
    // ──────────────────────────────────────────────────────────────
    // 基础枚举与辅助类型
    // ──────────────────────────────────────────────────────────────

    /// <summary>控件裁剪模式。</summary>
    public enum ClippingMode
    {
        None,
        ClipToBounds,
        ClipChildren
    }

    /// <summary>尺寸策略。</summary>
    public enum SizePolicy
    {
        Fixed,
        Auto,
        Fill,
        Percent
    }

    /// <summary>可见性状态。</summary>
    public enum EVisibility
    {
        Visible,
        Collapsed,
        Hidden,
        HitTestInvisible
    }

    // ──────────────────────────────────────────────────────────────
    // PaintContext - 绘制上下文
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 绘制上下文: 提供给 OnPaint 方法的绘制环境,
    /// 包含 CanvasRenderer 引用、裁剪区域、透明度、变换矩阵栈。
    /// </summary>
    public sealed class PaintContext
    {
        public CanvasRenderer Renderer { get; private set; }
        private readonly Stack<Rectangle> _clipStack = new();
        private readonly Stack<float> _opacityStack = new();
        public float CurrentOpacity { get; private set; } = 1f;
        public Rectangle CurrentClipRect => _clipStack.Count > 0 ? _clipStack.Peek() : Rectangle.Empty;

        public PaintContext(CanvasRenderer renderer)
        {
            Renderer = renderer;
        }

        public void PushClip(Rectangle rect)
        {
            if (_clipStack.Count > 0)
            {
                var parent = _clipStack.Peek();
                float x = Math.Max(rect.X, parent.X);
                float y = Math.Max(rect.Y, parent.Y);
                float r = Math.Min(rect.Right, parent.Right);
                float b = Math.Min(rect.Bottom, parent.Bottom);
                _clipStack.Push(new Rectangle(x, y, Math.Max(0, r - x), Math.Max(0, b - y)));
            }
            else
            {
                _clipStack.Push(rect);
            }
        }

        public void PopClip()
        {
            if (_clipStack.Count > 0) _clipStack.Pop();
        }

        public void PushOpacity(float opacity)
        {
            _opacityStack.Push(CurrentOpacity);
            CurrentOpacity *= Math.Clamp(opacity, 0f, 1f);
        }

        public void PopOpacity()
        {
            CurrentOpacity = _opacityStack.Count > 0 ? _opacityStack.Pop() : 1f;
        }

        public bool HasClip => _clipStack.Count > 0;
    }

    // ──────────────────────────────────────────────────────────────
    // WidgetBase - Widget 基类
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Widget 基类 — 所有 UI 控件的根抽象类。
    /// 提供完整的生命周期管理、变换系统、布局系统、事件系统和渲染管线。
    /// 相当于 UE UMG 的 UWidget / Unity 的 Graphic。
    /// </summary>
    public abstract class WidgetBase
    {
        // ── 标识 ──────────────────────────────────────────────────
        public string Name { get; set; } = "Widget";
        public object Tag { get; set; }
        public string TooltipText { get; set; } = string.Empty;

        // ── 生命周期状态 ──────────────────────────────────────────
        public bool IsConstructed { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsDestroyed { get; private set; }

        // ── 变换 ──────────────────────────────────────────────────
        public Vector2 Position { get; set; }
        public Vector2 Size { get; set; } = new Vector2(100, 32);
        public float Rotation { get; set; }
        public Vector2 Scale { get; set; } = Vector2.One;
        public Vector2 Pivot { get; set; } = new Vector2(0.5f, 0.5f);
        public int ZOrder { get; set; }

        // 锚点 (Min = 左上比例, Max = 右下比例, 0~1 范围)
        public Vector2 AnchorMin { get; set; } = Vector2.Zero;
        public Vector2 AnchorMax { get; set; } = Vector2.Zero;

        // 锚点偏移 (像素)
        public float OffsetLeft { get; set; }
        public float OffsetTop { get; set; }
        public float OffsetRight { get; set; }
        public float OffsetBottom { get; set; }

        // ── 布局 ──────────────────────────────────────────────────
        public EVisibility Visibility { get; set; } = EVisibility.Visible;
        public bool IsEnabled { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public Color TintColor { get; set; } = Color.White;
        public ClippingMode Clipping { get; set; } = ClippingMode.None;
        public bool SizeToContent { get; set; }
        public Vector2 MinSize { get; set; } = new Vector2(0, 0);
        public Vector2 MaxSize { get; set; } = new Vector2(4096, 4096);
        public Margins Margin { get; set; } = Margins.Zero;
        public Margins Padding { get; set; } = Margins.Zero;

        // 布局缓存
        public Rectangle CachedRect { get; protected set; }
        public Matrix4x4 LocalTransform { get; protected set; } = Matrix4x4.Identity;
        public Matrix4x4 AbsoluteTransform { get; protected set; } = Matrix4x4.Identity;
        public bool LayoutDirty { get; protected set; } = true;

        // ── 父子关系 ──────────────────────────────────────────────
        public WidgetBase Parent { get; private set; }
        private readonly List<WidgetBase> _children = new();
        public IReadOnlyList<WidgetBase> Children => _children;

        // ── 事件 ──────────────────────────────────────────────────
        public event Action<WidgetBase, Vector2, MouseButton> MouseDown;
        public event Action<WidgetBase, Vector2, MouseButton> MouseUp;
        public event Action<WidgetBase, Vector2, MouseButton> Click;
        public event Action<WidgetBase> MouseEnter;
        public event Action<WidgetBase> MouseLeave;
        public event Action<WidgetBase, Vector2> MouseMove;
        public event Action<WidgetBase, Vector2> DragStart;
        public event Action<WidgetBase, Vector2> DragMove;
        public event Action<WidgetBase, Vector2> DragEnd;
        public event Action<WidgetBase, InputKey> KeyDown;
        public event Action<WidgetBase, InputKey> KeyUp;
        public event Action<WidgetBase, char> TextInput;
        public event Action<WidgetBase> FocusGained;
        public event Action<WidgetBase> FocusLost;
        public event Action<WidgetBase, float> Scroll;
        public event Action<WidgetBase> Hover;
        public event Action<WidgetBase> Unhover;

        // ── 构造与生命周期 ──────────────────────────────────────
        protected WidgetBase()
        {
        }

        /// <summary>构造时调用。用于初始化内部数据。</summary>
        protected virtual void OnConstruct() { }

        /// <summary>初始化时调用。用于创建子 Widget。</summary>
        protected virtual void OnInitialize() { }

        /// <summary>游戏开始时调用。</summary>
        protected virtual void OnBeginPlay() { }

        /// <summary>游戏结束时调用。</summary>
        protected virtual void OnEndPlay() { }

        /// <summary>销毁时调用。清理资源。</summary>
        protected virtual void OnDestroy() { }

        public void Construct()
        {
            if (IsConstructed) return;
            IsConstructed = true;
            OnConstruct();
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;
            Construct();
            OnInitialize();
            foreach (var child in _children)
                child.Initialize();
        }

        public void BeginPlay()
        {
            if (IsPlaying) return;
            IsPlaying = true;
            Initialize();
            OnBeginPlay();
            foreach (var child in _children)
                child.BeginPlay();
        }

        public void EndPlay()
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            OnEndPlay();
            foreach (var child in _children)
                child.EndPlay();
        }

        public void Destroy()
        {
            if (IsDestroyed) return;
            IsDestroyed = true;
            EndPlay();
            OnDestroy();
            ClearChildren();
            Parent = null;
        }

        // ── 父子关系管理 ──────────────────────────────────────────
        public virtual void AddChild(WidgetBase child)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            if (child.Parent != null) child.RemoveFromParent();
            child.Parent = this;
            _children.Add(child);
            child.LayoutDirty = true;
            MarkLayoutDirty();
            if (IsInitialized) child.Initialize();
            if (IsPlaying) child.BeginPlay();
        }

        public virtual bool RemoveChild(WidgetBase child)
        {
            if (child == null || child.Parent != this) return false;
            child.Parent = null;
            _children.Remove(child);
            child.Destroy();
            MarkLayoutDirty();
            return true;
        }

        public void ClearChildren()
        {
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                var child = _children[i];
                child.Parent = null;
                child.Destroy();
            }
            _children.Clear();
            MarkLayoutDirty();
        }

        public void RemoveFromParent()
        {
            Parent?.RemoveChild(this);
        }

        public WidgetBase FindChild(string name)
        {
            if (Name == name) return this;
            foreach (var child in _children)
            {
                var found = child.FindChild(name);
                if (found != null) return found;
            }
            return null;
        }

        public List<WidgetBase> GetAllChildren()
        {
            var result = new List<WidgetBase>();
            CollectChildren(result);
            return result;
        }

        private void CollectChildren(List<WidgetBase> list)
        {
            foreach (var child in _children)
            {
                list.Add(child);
                child.CollectChildren(list);
            }
        }

        public int GetChildIndex(WidgetBase child) => _children.IndexOf(child);

        public void SetChildIndex(WidgetBase child, int index)
        {
            int current = _children.IndexOf(child);
            if (current < 0) return;
            _children.RemoveAt(current);
            index = Math.Clamp(index, 0, _children.Count);
            _children.Insert(index, child);
            MarkLayoutDirty();
        }

        // ── 布局系统 ──────────────────────────────────────────────
        public virtual Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            return Size;
        }

        public virtual void Arrange(Rectangle finalRect)
        {
            CachedRect = finalRect;
            Position = new Vector2(finalRect.X, finalRect.Y);
            Size = new Vector2(finalRect.Width, finalRect.Height);
            LayoutDirty = false;

            // 计算局部变换矩阵
            float pivotX = Size.X * Pivot.X;
            float pivotY = Size.Y * Pivot.Y;
            LocalTransform = Matrix4x4.CreateTranslation(-pivotX, -pivotY, 0)
                           * Matrix4x4.CreateRotationZ(Rotation * (MathF.PI / 180f))
                           * Matrix4x4.CreateScale(Scale.X, Scale.Y, 1f)
                           * Matrix4x4.CreateTranslation(Position.X + pivotX, Position.Y + pivotY, 0);

            if (Parent != null)
            {
                AbsoluteTransform = LocalTransform * Parent.AbsoluteTransform;
            }
            else
            {
                AbsoluteTransform = LocalTransform;
            }
        }

        public void MarkLayoutDirty()
        {
            LayoutDirty = true;
            Parent?.MarkLayoutDirty();
        }

        /// <summary>将本地坐标转换为绝对（屏幕）坐标。</summary>
        public Vector2 LocalToAbsolute(Vector2 localPoint)
        {
            return Vector2.Transform(localPoint, AbsoluteTransform);
        }

        /// <summary>将绝对（屏幕）坐标转换为本地坐标。</summary>
        public Vector2 AbsoluteToLocal(Vector2 absolutePoint)
        {
            Matrix4x4.Invert(AbsoluteTransform, out Matrix4x4 inv);
            return Vector2.Transform(absolutePoint, inv);
        }

        /// <summary>子类重写以定义子控件排列规则。</summary>
        public virtual void ArrangeChildren() { }

        /// <summary>计算此控件的屏幕空间矩形（用于命中测试）。</summary>
        public virtual Rectangle GetScreenRect()
        {
            Vector2 topLeft = LocalToAbsolute(Vector2.Zero);
            Vector2 bottomRight = LocalToAbsolute(Size);
            float x = Math.Min(topLeft.X, bottomRight.X);
            float y = Math.Min(topLeft.Y, bottomRight.Y);
            float w = Math.Abs(bottomRight.X - topLeft.X);
            float h = Math.Abs(bottomRight.Y - topLeft.Y);
            return new Rectangle(x, y, w, h);
        }

        // ── 事件虚方法 ────────────────────────────────────────────
        protected internal virtual void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            MouseDown?.Invoke(this, localPos, button);
        }
        protected internal virtual void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            MouseUp?.Invoke(this, localPos, button);
        }
        protected internal virtual void OnClick(Vector2 localPos, MouseButton button)
        {
            Click?.Invoke(this, localPos, button);
        }
        protected internal virtual void OnMouseEnter()
        {
            MouseEnter?.Invoke(this);
            Hover?.Invoke(this);
        }
        protected internal virtual void OnMouseLeave()
        {
            MouseLeave?.Invoke(this);
            Unhover?.Invoke(this);
        }
        protected internal virtual void OnMouseMove(Vector2 localPos)
        {
            MouseMove?.Invoke(this, localPos);
        }
        protected internal virtual void OnDragStart(Vector2 localPos)
        {
            DragStart?.Invoke(this, localPos);
        }
        protected internal virtual void OnDragMove(Vector2 localPos)
        {
            DragMove?.Invoke(this, localPos);
        }
        protected internal virtual void OnDragEnd(Vector2 localPos)
        {
            DragEnd?.Invoke(this, localPos);
        }
        protected internal virtual void OnKeyDown(InputKey key)
        {
            KeyDown?.Invoke(this, key);
        }
        protected internal virtual void OnKeyUp(InputKey key)
        {
            KeyUp?.Invoke(this, key);
        }
        protected internal virtual void OnTextInput(char c)
        {
            TextInput?.Invoke(this, c);
        }
        protected internal virtual void OnFocusGained()
        {
            FocusGained?.Invoke(this);
        }
        protected internal virtual void OnFocusLost()
        {
            FocusLost?.Invoke(this);
        }
        protected internal virtual void OnScroll(float delta)
        {
            Scroll?.Invoke(this, delta);
        }

        // ── 渲染 ──────────────────────────────────────────────────
        /// <summary>
        /// 绘制虚方法。子类重写以实现自定义绘制。
        /// </summary>
        /// <param name="context">绘制上下文（包含 CanvasRenderer 和裁剪信息）。</param>
        /// <param name="rect">控件的最终绘制区域（绝对坐标）。</param>
        protected internal virtual void OnPaint(PaintContext context, Rectangle rect) { }

        /// <summary>每帧更新逻辑（动画、状态等）。</summary>
        public virtual void Tick(float deltaTime) { }

        /// <summary>是否命中此控件（用于命中测试）。</summary>
        public virtual bool IsHitTestVisible
        {
            get
            {
                if (Visibility == EVisibility.Collapsed || Visibility == EVisibility.Hidden)
                    return false;
                if (!IsEnabled) return false;
                if (Visibility == EVisibility.HitTestInvisible) return false;
                return true;
            }
        }

        /// <summary>获取此控件的绘制顺序值（全局 ZOrder）。</summary>
        public virtual float GetDrawOrder()
        {
            float order = ZOrder;
            var p = Parent;
            while (p != null)
            {
                order += p.ZOrder * 1000f;
                p = p.Parent;
            }
            return order;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Rectangle 辅助结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>二维矩形（浮点精度）。</summary>
    public struct Rectangle
    {
        public float X, Y, Width, Height;
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public Vector2 Position => new Vector2(X, Y);
        public Vector2 Size => new Vector2(Width, Height);

        public Rectangle(float x, float y, float w, float h)
        {
            X = x; Y = y; Width = w; Height = h;
        }

        public Rectangle(Vector2 pos, Vector2 size)
        {
            X = pos.X; Y = pos.Y; Width = size.X; Height = size.Y;
        }

        public static readonly Rectangle Empty = new Rectangle(0, 0, 0, 0);

        public bool Contains(Vector2 point) =>
            point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

        public bool Overlaps(Rectangle other) =>
            X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

        public Rectangle Intersect(Rectangle other)
        {
            float x = Math.Max(X, other.X);
            float y = Math.Max(Y, other.Y);
            float r = Math.Min(Right, other.Right);
            float b = Math.Min(Bottom, other.Bottom);
            return new Rectangle(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
        }

        public Rectangle Offset(float dx, float dy) => new Rectangle(X + dx, Y + dy, Width, Height);
        public Rectangle Inflate(float dx, float dy) => new Rectangle(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);

        public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}x{Height:F1})";
    }

    /// <summary>鼠标按钮枚举。</summary>
    public enum MouseButton
    {
        Left,
        Right,
        Middle,
        XButton1,
        XButton2
    }
}
