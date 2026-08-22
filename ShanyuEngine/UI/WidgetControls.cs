using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ShanyuEngine.Input;

namespace ShanyuEngine.UI
{
    // ──────────────────────────────────────────────────────────────
    // TextBlock - 文本控件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 文本控件 — 显示格式化文本。
    /// 相当于 UE TextBlock / Unity Text。
    /// 支持阴影、描边和基本富文本标记。
    /// </summary>
    public class TextBlock : WidgetBase
    {
        public string Text { get; set; } = string.Empty;
        public string FontName { get; set; } = "Default";
        public float FontSize { get; set; } = 14f;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public Color Color { get; set; } = Color.White;
        public Color ShadowColor { get; set; } = Color.Transparent;
        public Vector2 ShadowOffset { get; set; } = new Vector2(1, 1);
        public Color OutlineColor { get; set; } = Color.Transparent;
        public float OutlineSize { get; set; }
        public HAlign HorizontalAlignment { get; set; } = HAlign.Left;
        public VAlign VerticalAlignment { get; set; } = VAlign.Top;
        public bool AutoWrap { get; set; }
        public bool RichText { get; set; }
        public float LineSpacing { get; set; } = 1.3f;
        public float LetterSpacing { get; set; }

        /// <summary>文本截断模式（省略号）。</summary>
        public bool TruncateWithEllipsis { get; set; }

        public TextBlock()
        {
            Name = "TextBlock";
            SizeToContent = true;
        }

        public TextBlock(string text) : this()
        {
            Text = text;
        }

        /// <summary>设置文本（链式调用）。</summary>
        public TextBlock SetText(string text) { Text = text; MarkLayoutDirty(); return this; }
        public TextBlock SetFontSize(float size) { FontSize = size; MarkLayoutDirty(); return this; }
        public TextBlock SetColor(Color color) { Color = color; return this; }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            if (string.IsNullOrEmpty(Text))
                return Vector2.Zero;

            float charWidth = FontSize * 0.6f;
            float lineHeight = FontSize * LineSpacing;

            if (AutoWrap && availableSize.X > 0)
            {
                int charsPerLine = (int)(availableSize.X / Math.Max(charWidth, 1));
                if (charsPerLine < 1) charsPerLine = 1;
                int lineCount = (int)Math.Ceiling((float)Text.Length / charsPerLine);
                return new Vector2(availableSize.X, lineHeight * lineCount);
            }

            return new Vector2(Text.Length * charWidth, lineHeight);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            if (string.IsNullOrEmpty(Text)) return;

            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;
            var finalColor = Color.WithAlpha(Color.A * opacity);

            Vector2 textPos = rect.Position;
            Vector2 textSize = rect.Size;

            // 垂直对齐
            float lineHeight = FontSize * LineSpacing;
            float textHeight = lineHeight;

            if (VerticalAlignment == VAlign.Center)
                textPos.Y += (textSize.Y - textHeight) * 0.5f;
            else if (VerticalAlignment == VAlign.Bottom)
                textPos.Y += textSize.Y - textHeight;

            // 绘制阴影
            if (ShadowColor.A > 0)
            {
                var shadowPos = textPos + ShadowOffset;
                renderer.DrawText(shadowPos, textSize, Text, FontName, FontSize,
                    ShadowColor.WithAlpha(ShadowColor.A * opacity),
                    HorizontalAlignment, VerticalAlignment, AutoWrap, RichText);
            }

            // 绘制文本
            renderer.DrawText(textPos, textSize, Text, FontName, FontSize,
                finalColor, HorizontalAlignment, VerticalAlignment, AutoWrap, RichText);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Image - 图片控件
    // ──────────────────────────────────────────────────────────────

    /// <summary>图片拉伸模式。</summary>
    public enum ImageStretch
    {
        Fill,
        Uniform,
        Fit,
        Stretch,
        Tile,
        Center
    }

    /// <summary>
    /// 图片控件 — 显示纹理或纯色。
    /// 相当于 UE Image / Unity Image。
    /// </summary>
    public class Image : WidgetBase
    {
        /// <summary>纹理资源名（由资源管理器解析）。</summary>
        public string TextureName { get; set; } = string.Empty;
        /// <summary>材质接口。</summary>
        public object MaterialInterface { get; set; }
        public ImageStretch Stretch { get; set; } = ImageStretch.Fill;
        public Color TintColor { get; set; } = Color.White;
        public Vector4 UVRegion { get; set; } = new Vector4(0, 0, 1, 1);

        // 圆角
        public float CornerRadius { get; set; }

        // 九宫格边距
        public float NineSliceMargin { get; set; } = 8f;

        public Image()
        {
            Name = "Image";
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            // 默认返回当前尺寸
            return Size;
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            var finalColor = TintColor.WithAlpha(TintColor.A * context.CurrentOpacity);

            if (!string.IsNullOrEmpty(TextureName))
            {
                if (NineSliceMargin > 0)
                {
                    renderer.DrawNineSlice(rect.Position, rect.Size, TextureName,
                        UVRegion, NineSliceMargin, finalColor);
                }
                else
                {
                    renderer.DrawTexture(rect.Position, rect.Size, TextureName,
                        UVRegion, finalColor);
                }
            }
            else
            {
                // 纯色填充
                if (CornerRadius > 0)
                    renderer.FillRoundedRect(rect.Position, rect.Size, CornerRadius, finalColor);
                else
                    renderer.FillRect(rect.Position, rect.Size, finalColor);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Button - 按钮控件
    // ──────────────────────────────────────────────────────────────

    /// <summary>按钮点击触发方式。</summary>
    public enum ClickMethod
    {
        Down,   // 按下时触发
        Up,     // 抬起时触发（默认）
        Press   // 按下一段时间后触发
    }

    /// <summary>按钮状态。</summary>

    /// <summary>
    /// 按钮控件 — 支持多状态样式和点击事件。
    /// 相当于 UE Button / Unity Button。
    /// </summary>
    public class Button : WidgetBase
    {
        // 样式属性
        public Color NormalColor { get; set; } = new Color(0.26f, 0.27f, 0.32f, 1f);
        public Color HoveredColor { get; set; } = new Color(0.3f, 0.31f, 0.36f, 1f);
        public Color PressedColor { get; set; } = new Color(0.2f, 0.22f, 0.27f, 1f);
        public Color DisabledColor { get; set; } = new Color(0.2f, 0.2f, 0.22f, 0.6f);

        public string NormalTexture { get; set; } = string.Empty;
        public string HoveredTexture { get; set; } = string.Empty;
        public string PressedTexture { get; set; } = string.Empty;
        public string DisabledTexture { get; set; } = string.Empty;

        public Color TextColor { get; set; } = Color.White;
        public Color TextShadowColor { get; set; } = new Color(0, 0, 0, 0.3f);
        public Vector2 TextShadowOffset { get; set; } = new Vector2(1, 1);

        public ClickMethod ClickMethod { get; set; } = ClickMethod.Up;
        public float CornerRadius { get; set; } = 3f;

        // 状态
        public ButtonState CurrentState { get; private set; } = ButtonState.Normal;
        public bool IsHovered { get; private set; }
        public bool IsPressed { get; private set; }

        // 事件
        public event Action<Button> OnClicked;
        public event Action<Button> OnPressed;
        public event Action<Button> OnReleased;
        public event Action<Button> OnHovered;
        public event Action<Button> OnUnhovered;

        // 内容文本
        public string Text { get; set; } = "Button";
        public float FontSize { get; set; } = 14f;
        public string FontName { get; set; } = "Default";

        // 内容图片
        public string ImageTexture { get; set; } = string.Empty;
        public Vector2 ImageSize { get; set; } = new Vector2(16, 16);
        public float ImageTextSpacing { get; set; } = 6f;

        public Button()
        {
            Name = "Button";
        }

        public Button(string text) : this()
        {
            Text = text;
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            float textW = Text.Length * FontSize * 0.6f;
            float imgW = !string.IsNullOrEmpty(ImageTexture) ? ImageSize.X + ImageTextSpacing : 0;
            float h = Math.Max(FontSize * 1.6f, ImageSize.Y);
            return new Vector2(textW + imgW + 24, h + 12);
        }

        protected internal override void OnMouseEnter()
        {
            base.OnMouseEnter();
            IsHovered = true;
            CurrentState = ButtonState.Hovered;
            OnHovered?.Invoke(this);
        }

        protected internal override void OnMouseLeave()
        {
            base.OnMouseLeave();
            IsHovered = false;
            IsPressed = false;
            CurrentState = IsEnabled ? ButtonState.Normal : ButtonState.Disabled;
            OnUnhovered?.Invoke(this);
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            if (!IsEnabled || button != MouseButton.Left) return;
            IsPressed = true;
            CurrentState = ButtonState.Pressed;
            OnPressed?.Invoke(this);

            if (ClickMethod == ClickMethod.Down)
                OnClicked?.Invoke(this);
        }

        protected internal override void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            base.OnMouseUp(localPos, button);
            if (!IsEnabled || button != MouseButton.Left) return;
            IsPressed = false;
            CurrentState = IsHovered ? ButtonState.Hovered : ButtonState.Normal;
            OnReleased?.Invoke(this);

            if (ClickMethod == ClickMethod.Up)
                OnClicked?.Invoke(this);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;

            // 选择颜色/纹理
            Color bgColor;
            string texture = string.Empty;

            if (!IsEnabled)
            {
                bgColor = DisabledColor.WithAlpha(DisabledColor.A * opacity);
                texture = DisabledTexture;
            }
            else
            {
                switch (CurrentState)
                {
                    case ButtonState.Pressed:
                        bgColor = PressedColor.WithAlpha(PressedColor.A * opacity);
                        texture = PressedTexture;
                        break;
                    case ButtonState.Hovered:
                        bgColor = HoveredColor.WithAlpha(HoveredColor.A * opacity);
                        texture = HoveredTexture;
                        break;
                    default:
                        bgColor = NormalColor.WithAlpha(NormalColor.A * opacity);
                        texture = NormalTexture;
                        break;
                }
            }

            // 绘制背景
            if (!string.IsNullOrEmpty(texture))
                renderer.DrawTexture(rect.Position, rect.Size, texture, new Vector4(0, 0, 1, 1), bgColor);
            else
                renderer.FillRoundedRect(rect.Position, rect.Size, CornerRadius, bgColor);

            // 绘制图片
            if (!string.IsNullOrEmpty(ImageTexture))
            {
                float imgX = rect.X + (rect.Width - ImageSize.X) * 0.5f;
                float imgY = rect.Y + (rect.Height - ImageSize.Y) * 0.5f - FontSize * 0.3f;
                if (!string.IsNullOrEmpty(Text))
                {
                    float totalW = ImageSize.X + ImageTextSpacing + Text.Length * FontSize * 0.6f;
                    imgX = rect.X + (rect.Width - totalW) * 0.5f;
                }
                renderer.DrawTexture(new Vector2(imgX, imgY), ImageSize,
                    ImageTexture, new Vector4(0, 0, 1, 1), TextColor.WithAlpha(opacity));
            }

            // 绘制文本
            if (!string.IsNullOrEmpty(Text))
            {
                float textX = rect.X;
                if (!string.IsNullOrEmpty(ImageTexture))
                {
                    float totalW = ImageSize.X + ImageTextSpacing + Text.Length * FontSize * 0.6f;
                    textX = rect.X + (rect.Width - totalW) * 0.5f + ImageSize.X + ImageTextSpacing;
                }
                renderer.DrawText(new Vector2(textX, rect.Y), rect.Size, Text, FontName, FontSize,
                    TextColor.WithAlpha(TextColor.A * opacity),
                    HAlign.Center, VAlign.Center, false, false);

                // 文本阴影
                if (TextShadowColor.A > 0)
                {
                    renderer.DrawText(new Vector2(textX, rect.Y) + TextShadowOffset, rect.Size,
                        Text, FontName, FontSize,
                        TextShadowColor.WithAlpha(TextShadowColor.A * opacity),
                        HAlign.Center, VAlign.Center, false, false);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // TextBlockButton - 文本按钮
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 文本按钮 — Button + TextBlock 的组合便捷类。
    /// </summary>
    public class TextBlockButton : WidgetBase
    {
        public readonly TextBlock Label;
        public Color NormalTextColor { get; set; } = Color.White;
        public Color HoveredTextColor { get; set; } = new Color(0.8f, 0.9f, 1f, 1f);
        public Color PressedTextColor { get; set; } = new Color(0.6f, 0.7f, 0.9f, 1f);

        public bool IsHovered { get; private set; }
        public bool IsPressed { get; private set; }

        public event Action<TextBlockButton> OnClicked;

        public string Text
        {
            get => Label.Text;
            set => Label.Text = value;
        }

        public TextBlockButton()
        {
            Name = "TextBlockButton";
            Label = new TextBlock
            {
                Name = "Label",
                HorizontalAlignment = HAlign.Center,
                VerticalAlignment = VAlign.Center
            };
            base.AddChild(Label);
        }

        protected internal override void OnMouseEnter()
        {
            base.OnMouseEnter();
            IsHovered = true;
            Label.Color = HoveredTextColor;
        }

        protected internal override void OnMouseLeave()
        {
            base.OnMouseLeave();
            IsHovered = false;
            IsPressed = false;
            Label.Color = NormalTextColor;
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            if (button != MouseButton.Left) return;
            IsPressed = true;
            Label.Color = PressedTextColor;
        }

        protected internal override void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            base.OnMouseUp(localPos, button);
            if (!IsPressed || button != MouseButton.Left) return;
            IsPressed = false;
            Label.Color = IsHovered ? HoveredTextColor : NormalTextColor;
            OnClicked?.Invoke(this);
        }

        public override void ArrangeChildren()
        {
            if (Children.Count > 0)
                Children[0].Arrange(CachedRect);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ImageButton - 图片按钮
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 图片按钮 — Button + Image 的组合便捷类。
    /// </summary>
    public class ImageButton : WidgetBase
    {
        public readonly Image Icon;

        public string NormalTexture { get; set; } = string.Empty;
        public string HoveredTexture { get; set; } = string.Empty;
        public string PressedTexture { get; set; } = string.Empty;

        public Color NormalTint { get; set; } = Color.White;
        public Color HoveredTint { get; set; } = Color.White;
        public Color PressedTint { get; set; } = new Color(0.8f, 0.8f, 0.8f, 1f);

        public bool IsHovered { get; private set; }
        public bool IsPressed { get; private set; }

        public event Action<ImageButton> OnClicked;

        public ImageButton()
        {
            Name = "ImageButton";
            Icon = new Image { Name = "Icon" };
            base.AddChild(Icon);
        }

        protected internal override void OnMouseEnter()
        {
            base.OnMouseEnter();
            IsHovered = true;
            Icon.TextureName = HoveredTexture;
            Icon.TintColor = HoveredTint;
        }

        protected internal override void OnMouseLeave()
        {
            base.OnMouseLeave();
            IsHovered = false;
            IsPressed = false;
            Icon.TextureName = NormalTexture;
            Icon.TintColor = NormalTint;
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            if (button != MouseButton.Left) return;
            IsPressed = true;
            Icon.TextureName = PressedTexture;
            Icon.TintColor = PressedTint;
        }

        protected internal override void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            base.OnMouseUp(localPos, button);
            if (!IsPressed || button != MouseButton.Left) return;
            IsPressed = false;
            Icon.TextureName = IsHovered ? HoveredTexture : NormalTexture;
            Icon.TintColor = IsHovered ? HoveredTint : NormalTint;
            OnClicked?.Invoke(this);
        }

        public override void ArrangeChildren()
        {
            if (Children.Count > 0)
                Children[0].Arrange(CachedRect);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // EditableTextBox - 可编辑文本框
    // ──────────────────────────────────────────────────────────────

    /// <summary>文本提交类型。</summary>
    public enum TextCommitType
    {
        Default,
        OnEnter,
        OnTab,
        OnFocusLost
    }

    /// <summary>
    /// 可编辑文本框 — 支持光标、选中、剪贴板和密码输入。
    /// 相当于 UE EditableTextBox / Unity InputField。
    /// </summary>
    public class EditableTextBox : WidgetBase
    {
        // 文本
        private StringBuilder _textBuilder = new();
        public string Text
        {
            get => _textBuilder.ToString();
            set
            {
                _textBuilder = new StringBuilder(value ?? string.Empty);
                if (_textBuilder.Length > MaxLength)
                    _textBuilder.Length = MaxLength;
                _cursorIndex = Math.Min(_cursorIndex, _textBuilder.Length);
                _selectionStart = _cursorIndex;
                MarkLayoutDirty();
            }
        }

        public string PlaceholderText { get; set; } = "Enter text...";
        public int MaxLength { get; set; } = 256;
        public bool IsPassword { get; set; }
        public bool IsMultiline { get; set; }

        // 字体
        public string FontName { get; set; } = "Default";
        public float FontSize { get; set; } = 14f;
        public Color TextColor { get; set; } = Color.White;
        public Color PlaceholderColor { get; set; } = new Color(0.5f, 0.5f, 0.55f, 1f);
        public Color BackgroundColor { get; set; } = new Color(0.18f, 0.19f, 0.23f, 1f);
        public Color CaretColor { get; set; } = Color.White;
        public Color SelectionColor { get; set; } = new Color(0.26f, 0.59f, 0.98f, 0.4f);

        // 光标
        private int _cursorIndex;
        public int CursorIndex => _cursorIndex;
        private int _selectionStart;
        public bool HasSelection => _cursorIndex != _selectionStart;
        public string SelectedText
        {
            get
            {
                int min = Math.Min(_cursorIndex, _selectionStart);
                int max = Math.Max(_cursorIndex, _selectionStart);
                return HasSelection ? _textBuilder.ToString(min, max - min) : string.Empty;
            }
        }

        private float _caretBlinkTimer;
        private bool _caretVisible = true;
        public float CaretBlinkRate { get; set; } = 0.5f;

        // 事件
        public event Action<string> OnTextChanged;
        public event Action<string, TextCommitType> OnTextCommitted;

        // 按键重复
        private float _repeatTimer;
        private InputKey _repeatKey;

        public EditableTextBox()
        {
            Name = "EditableTextBox";
        }

        public EditableTextBox(string text) : this()
        {
            Text = text;
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            if (IsMultiline)
                return new Vector2(availableSize.X > 0 ? availableSize.X : 200, FontSize * 4 * 1.3f);
            return new Vector2(200, FontSize * 1.6f);
        }

        protected internal override void OnFocusGained()
        {
            base.OnFocusGained();
            _caretVisible = true;
            _caretBlinkTimer = 0;
        }

        protected internal override void OnFocusLost()
        {
            base.OnFocusLost();
            _caretVisible = false;
            OnTextCommitted?.Invoke(Text, TextCommitType.OnFocusLost);
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            // 根据点击位置计算光标位置
            float charWidth = FontSize * 0.6f;
            int index = (int)Math.Round((localPos.X - 4) / charWidth);
            _cursorIndex = Math.Clamp(index, 0, _textBuilder.Length);
            _selectionStart = _cursorIndex;
            _caretBlinkTimer = 0;
            _caretVisible = true;
        }

        protected internal override void OnKeyDown(InputKey key)
        {
            base.OnKeyDown(key);

            bool handled = true;
            bool shift = false; // 简化：实际应从 InputSystem 查询

            switch (key)
            {
                case InputKey.LeftArrow:
                    if (_cursorIndex > 0) _cursorIndex--;
                    break;
                case InputKey.RightArrow:
                    if (_cursorIndex < _textBuilder.Length) _cursorIndex++;
                    break;
                case InputKey.Home:
                    _cursorIndex = 0;
                    break;
                case InputKey.End:
                    _cursorIndex = _textBuilder.Length;
                    break;
                case InputKey.Backspace:
                    if (HasSelection)
                        DeleteSelection();
                    else if (_cursorIndex > 0)
                    {
                        _textBuilder.Remove(--_cursorIndex, 1);
                        OnTextChanged?.Invoke(Text);
                    }
                    break;
                case InputKey.Delete:
                    if (HasSelection)
                        DeleteSelection();
                    else if (_cursorIndex < _textBuilder.Length)
                    {
                        _textBuilder.Remove(_cursorIndex, 1);
                        OnTextChanged?.Invoke(Text);
                    }
                    break;
                case InputKey.Enter:
                    if (IsMultiline)
                    {
                        InsertChar('\n');
                    }
                    else
                    {
                        OnTextCommitted?.Invoke(Text, TextCommitType.OnEnter);
                    }
                    break;
                case InputKey.Escape:
                    OnTextCommitted?.Invoke(Text, TextCommitType.Default);
                    break;
                case InputKey.Tab:
                    OnTextCommitted?.Invoke(Text, TextCommitType.OnTab);
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled)
            {
                _selectionStart = _cursorIndex;
                _caretBlinkTimer = 0;
                _caretVisible = true;
            }
        }

        protected internal override void OnTextInput(char c)
        {
            if (c < 32) return;

            if (HasSelection)
                DeleteSelection();

            InsertChar(c);
        }

        private void InsertChar(char c)
        {
            if (_textBuilder.Length >= MaxLength) return;

            if (IsPassword && c != '\n')
                _textBuilder.Insert(_cursorIndex++, '*');
            else
                _textBuilder.Insert(_cursorIndex++, c);

            _selectionStart = _cursorIndex;
            OnTextChanged?.Invoke(Text);
        }

        private void DeleteSelection()
        {
            int min = Math.Min(_cursorIndex, _selectionStart);
            int max = Math.Max(_cursorIndex, _selectionStart);
            _textBuilder.Remove(min, max - min);
            _cursorIndex = min;
            _selectionStart = min;
            OnTextChanged?.Invoke(Text);
        }

        /// <summary>剪切选中的文本。</summary>
        public string CutSelectedText()
        {
            string sel = SelectedText;
            if (HasSelection) DeleteSelection();
            return sel;
        }

        /// <summary>在光标处插入文本。</summary>
        public void InsertText(string text)
        {
            if (HasSelection) DeleteSelection();
            foreach (char c in text)
            {
                if (_textBuilder.Length >= MaxLength) break;
                InsertChar(c);
            }
        }

        public override void Tick(float deltaTime)
        {
            // 光标闪烁
            _caretBlinkTimer += deltaTime;
            if (_caretBlinkTimer >= CaretBlinkRate)
            {
                _caretBlinkTimer -= CaretBlinkRate;
                _caretVisible = !_caretVisible;
            }
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;

            // 背景
            renderer.FillRect(rect.Position, rect.Size, BackgroundColor.WithAlpha(BackgroundColor.A * opacity));

            string displayText = IsPassword ? new string('*', _textBuilder.Length) : Text;
            float charWidth = FontSize * 0.6f;
            float textX = rect.X + 4;
            float textY = rect.Y + (rect.Height - FontSize * 1.3f) * 0.5f;

            if (string.IsNullOrEmpty(displayText) && !IsFocused())
            {
                // 占位文本
                renderer.DrawText(new Vector2(textX, textY), rect.Size, PlaceholderText,
                    FontName, FontSize, PlaceholderColor.WithAlpha(PlaceholderColor.A * opacity),
                    HAlign.Left, VAlign.Center, false, false);
            }
            else
            {
                // 绘制选中背景
                if (HasSelection && IsFocused())
                {
                    int min = Math.Min(_cursorIndex, _selectionStart);
                    int max = Math.Max(_cursorIndex, _selectionStart);
                    float selX = textX + min * charWidth;
                    float selW = (max - min) * charWidth;
                    renderer.FillRect(new Vector2(selX, rect.Y + 2),
                        new Vector2(selW, rect.Height - 4), SelectionColor);
                }

                // 绘制文本
                renderer.DrawText(new Vector2(textX, textY), rect.Size, displayText,
                    FontName, FontSize, TextColor.WithAlpha(TextColor.A * opacity),
                    HAlign.Left, VAlign.Center, false, false);

                // 绘制光标
                if (IsFocused() && _caretVisible)
                {
                    float caretX = textX + _cursorIndex * charWidth;
                    renderer.FillRect(new Vector2(caretX, rect.Y + 3),
                        new Vector2(Math.Max(1, FontSize * 0.08f), rect.Height - 6),
                        CaretColor.WithAlpha(CaretColor.A * opacity));
                }
            }
        }

        private bool IsFocused()
        {
            // 简化: 由 WidgetSystem 通过 FocusedWidget 判断
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Slider - 滑块
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 滑块控件 — 拖拽选择数值。
    /// 相当于 UE Slider / Unity Slider。
    /// </summary>
    public class Slider : WidgetBase
    {
        public float MinValue { get; set; }
        public float MaxValue { get; set; } = 1f;
        public float Value { get; set; } = 0.5f;
        public float StepSize { get; set; }

        /// <summary>方向。</summary>
        public bool IsVertical { get; set; }

        // 颜色
        public Color FillColor { get; set; } = new Color(0.26f, 0.59f, 0.98f, 1f);
        public Color TrackColor { get; set; } = new Color(0.2f, 0.2f, 0.25f, 1f);
        public Color ThumbColor { get; set; } = Color.White;

        public float ThumbSize { get; set; } = 14f;
        public float TrackThickness { get; set; } = 6f;

        private bool _isDragging;

        public event Action<float> OnValueChanged;

        public Slider()
        {
            Name = "Slider";
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            if (IsVertical)
                return new Vector2(Math.Max(ThumbSize, 20), 160);
            return new Vector2(160, Math.Max(ThumbSize, 20));
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            if (button != MouseButton.Left) return;
            _isDragging = true;
            UpdateValueFromPosition(localPos);
        }

        protected internal override void OnMouseMove(Vector2 localPos)
        {
            base.OnMouseMove(localPos);
            if (_isDragging)
                UpdateValueFromPosition(localPos);
        }

        protected internal override void OnMouseUp(Vector2 localPos, MouseButton button)
        {
            base.OnMouseUp(localPos, button);
            _isDragging = false;
        }

        protected internal override void OnScroll(float delta)
        {
            float range = MaxValue - MinValue;
            float step = StepSize > 0 ? StepSize : range * 0.1f;
            Value = Math.Clamp(Value + (delta > 0 ? step : -step), MinValue, MaxValue);
            OnValueChanged?.Invoke(Value);
        }

        private void UpdateValueFromPosition(Vector2 localPos)
        {
            float t;
            if (IsVertical)
                t = 1f - localPos.Y / Math.Max(Size.Y, 1);
            else
                t = localPos.X / Math.Max(Size.X, 1);

            t = Math.Clamp(t, 0, 1);
            float raw = MinValue + t * (MaxValue - MinValue);

            if (StepSize > 0)
                raw = MathF.Round(raw / StepSize) * StepSize;

            Value = Math.Clamp(raw, MinValue, MaxValue);
            OnValueChanged?.Invoke(Value);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;
            float t = (Value - MinValue) / Math.Max(MaxValue - MinValue, 0.0001f);

            Vector2 trackPos, trackSize, fillSize, thumbPos;

            if (IsVertical)
            {
                float cx = rect.X + rect.Width * 0.5f;
                trackPos = new Vector2(cx - TrackThickness * 0.5f, rect.Y);
                trackSize = new Vector2(TrackThickness, rect.Height);
                fillSize = new Vector2(TrackThickness, rect.Height * t);
                thumbPos = new Vector2(cx - ThumbSize * 0.5f, rect.Bottom - rect.Height * t - ThumbSize * 0.5f);
            }
            else
            {
                float cy = rect.Y + rect.Height * 0.5f;
                trackPos = new Vector2(rect.X, cy - TrackThickness * 0.5f);
                trackSize = new Vector2(rect.Width, TrackThickness);
                fillSize = new Vector2(rect.Width * t, TrackThickness);
                thumbPos = new Vector2(rect.X + rect.Width * t - ThumbSize * 0.5f, cy - ThumbSize * 0.5f);
            }

            // 轨道
            renderer.FillRect(trackPos, trackSize, TrackColor.WithAlpha(TrackColor.A * opacity));
            // 填充
            renderer.FillRect(trackPos, fillSize, FillColor.WithAlpha(FillColor.A * opacity));
            // 把手
            renderer.FillRoundedRect(thumbPos, new Vector2(ThumbSize, ThumbSize),
                ThumbSize * 0.5f, ThumbColor.WithAlpha(ThumbColor.A * opacity));
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ProgressBar - 进度条
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 进度条控件 — 显示百分比进度。
    /// 相当于 UE ProgressBar / Unity Slider(disable)。
    /// </summary>
    public class ProgressBar : WidgetBase
    {
        public float Percent { get; set; } = 0.5f;
        public float Min { get; set; }
        public float Max { get; set; } = 1f;

        public Color FillColor { get; set; } = new Color(0.26f, 0.59f, 0.98f, 1f);
        public Color BackgroundColor { get; set; } = new Color(0.15f, 0.16f, 0.19f, 1f);
        public Color BorderColor { get; set; } = new Color(0.25f, 0.26f, 0.3f, 0.4f);

        public float BorderThickness { get; set; } = 1f;
        public float CornerRadius { get; set; } = 2f;

        /// <summary>填充方向（0=左到右, 1=右到左, 2=下到上, 3=上到下）。</summary>
        public int FillDirection { get; set; }

        /// <summary>在进度条上叠加百分比文字。</summary>
        public bool ShowPercentText { get; set; }

        /// <summary>动画过渡速度（每秒变化比例）。</summary>
        public float AnimSpeed { get; set; } = 5f;
        private float _displayPercent;

        public ProgressBar()
        {
            Name = "ProgressBar";
            _displayPercent = Percent;
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            return new Vector2(200, 20);
        }

        public override void Tick(float deltaTime)
        {
            // 平滑动画过渡
            float target = (Percent - Min) / Math.Max(Max - Min, 0.0001f);
            target = Math.Clamp(target, 0, 1);
            if (Math.Abs(_displayPercent - target) > 0.001f)
            {
                _displayPercent += Math.Sign(target - _displayPercent) *
                    Math.Min(Math.Abs(target - _displayPercent), AnimSpeed * deltaTime);
            }
            else
            {
                _displayPercent = target;
            }
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;
            float t = Math.Clamp(_displayPercent, 0, 1);

            // 背景
            renderer.FillRoundedRect(rect.Position, rect.Size, CornerRadius,
                BackgroundColor.WithAlpha(BackgroundColor.A * opacity));

            // 填充
            Vector2 fillPos = rect.Position;
            Vector2 fillSize = rect.Size;
            switch (FillDirection)
            {
                case 0: // 左到右
                    fillSize.X = rect.Width * t;
                    break;
                case 1: // 右到左
                    fillPos.X = rect.Right - rect.Width * t;
                    fillSize.X = rect.Width * t;
                    break;
                case 2: // 下到上
                    fillPos.Y = rect.Bottom - rect.Height * t;
                    fillSize.Y = rect.Height * t;
                    break;
                case 3: // 上到下
                    fillSize.Y = rect.Height * t;
                    break;
            }

            if (t > 0.01f)
                renderer.FillRoundedRect(fillPos, fillSize, CornerRadius,
                    FillColor.WithAlpha(FillColor.A * opacity));

            // 边框
            if (BorderThickness > 0)
                renderer.DrawRect(rect.Position, rect.Size, BorderThickness,
                    BorderColor.WithAlpha(BorderColor.A * opacity));

            // 叠加文字
            if (ShowPercentText)
            {
                string pctText = $"{(Percent / Math.Max(Max, 0.0001f) * 100f):F0}%";
                renderer.DrawText(rect.Position, rect.Size, pctText, "Default", 12f,
                    Color.White.WithAlpha(opacity), HAlign.Center, VAlign.Center, false, false);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // CheckBox - 复选框
    // ──────────────────────────────────────────────────────────────

    /// <summary>复选框状态。</summary>
    public enum CheckBoxState
    {
        Unchecked,
        Checked,
        Indeterminate
    }

    /// <summary>
    /// 复选框控件 — 支持三态。
    /// 相当于 UE CheckBox / Unity Toggle。
    /// </summary>
    public class CheckBox : WidgetBase
    {
        public CheckBoxState State { get; set; } = CheckBoxState.Unchecked;
        public bool IsChecked
        {
            get => State == CheckBoxState.Checked;
            set => State = value ? CheckBoxState.Checked : CheckBoxState.Unchecked;
        }
        public bool IsThreeState { get; set; }

        public string Text { get; set; } = string.Empty;
        public float FontSize { get; set; } = 14f;

        public Color CheckedColor { get; set; } = new Color(0.26f, 0.59f, 0.98f, 1f);
        public Color UncheckedColor { get; set; } = new Color(0.2f, 0.2f, 0.25f, 1f);
        public Color IndeterminateColor { get; set; } = new Color(0.5f, 0.5f, 0.5f, 1f);
        public Color CheckMarkColor { get; set; } = Color.White;
        public Color TextColor { get; set; } = Color.White;

        public float BoxSize { get; set; } = 18f;

        public event Action<CheckBoxState> OnCheckStateChanged;

        public CheckBox()
        {
            Name = "CheckBox";
        }

        protected internal override void OnClick(Vector2 localPos, MouseButton button)
        {
            base.OnClick(localPos, button);
            if (button != MouseButton.Left) return;

            if (IsThreeState)
            {
                State = State switch
                {
                    CheckBoxState.Unchecked => CheckBoxState.Checked,
                    CheckBoxState.Checked => CheckBoxState.Indeterminate,
                    _ => CheckBoxState.Unchecked
                };
            }
            else
            {
                State = State == CheckBoxState.Checked ? CheckBoxState.Unchecked : CheckBoxState.Checked;
            }

            OnCheckStateChanged?.Invoke(State);
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            float textW = string.IsNullOrEmpty(Text) ? 0 : Text.Length * FontSize * 0.6f + 6;
            return new Vector2(BoxSize + textW, Math.Max(BoxSize, FontSize * 1.3f));
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;

            // 复选框框
            Color boxColor = State switch
            {
                CheckBoxState.Checked => CheckedColor,
                CheckBoxState.Indeterminate => IndeterminateColor,
                _ => UncheckedColor
            };

            float boxY = rect.Y + (rect.Height - BoxSize) * 0.5f;
            Vector2 boxPos = new Vector2(rect.X, boxY);
            Vector2 boxSizeV = new Vector2(BoxSize, BoxSize);

            renderer.FillRoundedRect(boxPos, boxSizeV, 3f, boxColor.WithAlpha(boxColor.A * opacity));
            renderer.DrawRect(boxPos, boxSizeV, 1f, new Color(0.5f, 0.5f, 0.5f, 0.5f * opacity));

            // 勾选标记
            if (State == CheckBoxState.Checked)
            {
                // 简化勾号：填充一个较小的矩形
                float margin = BoxSize * 0.2f;
                renderer.FillRect(boxPos + new Vector2(margin, margin),
                    boxSizeV - new Vector2(margin * 2, margin * 2),
                    CheckMarkColor.WithAlpha(CheckMarkColor.A * opacity));
            }
            else if (State == CheckBoxState.Indeterminate)
            {
                float margin = BoxSize * 0.25f;
                renderer.FillRect(boxPos + new Vector2(margin, BoxSize * 0.4f),
                    new Vector2(BoxSize - margin * 2, BoxSize * 0.2f),
                    CheckMarkColor.WithAlpha(CheckMarkColor.A * opacity));
            }

            // 标签文本
            if (!string.IsNullOrEmpty(Text))
            {
                float textX = rect.X + BoxSize + 6;
                renderer.DrawText(new Vector2(textX, rect.Y), rect.Size, Text,
                    "Default", FontSize, TextColor.WithAlpha(TextColor.A * opacity),
                    HAlign.Left, VAlign.Center, false, false);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ComboBox - 下拉选择框
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 下拉选择框 — 选项列表选择。
    /// 相当于 UE ComboBox / Unity Dropdown。
    /// </summary>
    public class ComboBox : WidgetBase
    {
        public List<string> Options { get; set; } = new();
        public int SelectedIndex { get; set; } = -1;
        public string SelectedItem => SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : null;

        public Color BackgroundColor { get; set; } = new Color(0.18f, 0.19f, 0.23f, 1f);
        public Color DropdownBackgroundColor { get; set; } = new Color(0.15f, 0.16f, 0.19f, 1f);
        public Color TextColor { get; set; } = Color.White;
        public Color HoveredItemColor { get; set; } = new Color(0.26f, 0.27f, 0.32f, 1f);
        public Color SelectedItemColor { get; set; } = new Color(0.26f, 0.59f, 0.98f, 0.3f);

        public float FontSize { get; set; } = 14f;
        public float ItemHeight { get; set; } = 24f;
        public float DropdownMaxHeight { get; set; } = 200f;

        public bool IsOpen { get; private set; }
        private int _hoveredIndex = -1;

        // 事件
        public event Action<int, string> OnSelectionChanged;

        public ComboBox()
        {
            Name = "ComboBox";
        }

        protected internal override void OnMouseDown(Vector2 localPos, MouseButton button)
        {
            base.OnMouseDown(localPos, button);
            if (button != MouseButton.Left) return;

            if (IsOpen)
            {
                // 检查是否点击了某个选项
                float dropdownY = CachedRect.Bottom;
                float itemY = localPos.Y - (dropdownY - CachedRect.Y);
                int idx = (int)(itemY / ItemHeight);
                if (idx >= 0 && idx < Options.Count)
                {
                    SelectedIndex = idx;
                    OnSelectionChanged?.Invoke(idx, Options[idx]);
                }
                IsOpen = false;
            }
            else
            {
                IsOpen = true;
            }
        }

        protected internal override void OnMouseMove(Vector2 localPos)
        {
            base.OnMouseMove(localPos);
            if (IsOpen)
            {
                float dropdownY = CachedRect.Bottom;
                float itemY = localPos.Y - (dropdownY - CachedRect.Y);
                _hoveredIndex = (int)(itemY / ItemHeight);
            }
        }

        public override Vector2 ComputeDesiredSize(Vector2 availableSize)
        {
            return new Vector2(160, ItemHeight);
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;

            // 主框
            renderer.FillRect(rect.Position, rect.Size,
                BackgroundColor.WithAlpha(BackgroundColor.A * opacity));
            renderer.DrawRect(rect.Position, rect.Size, 1f,
                new Color(0.4f, 0.4f, 0.45f, 0.6f * opacity));

            // 选中文本
            string display = SelectedItem ?? "-- Select --";
            renderer.DrawText(rect.Position + new Vector2(6, 0), rect.Size, display,
                "Default", FontSize, TextColor.WithAlpha(TextColor.A * opacity),
                HAlign.Left, VAlign.Center, false, false);

            // 下拉箭头
            float arrowSize = 6;
            float arrowX = rect.Right - 14;
            float arrowY = rect.Y + rect.Height * 0.5f;
            // 简化箭头为三角

            // 下拉列表
            if (IsOpen)
            {
                float dropdownY = rect.Bottom;
                float dropdownH = Math.Min(Options.Count * ItemHeight, DropdownMaxHeight);

                renderer.FillRect(new Vector2(rect.X, dropdownY),
                    new Vector2(rect.Width, dropdownH),
                    DropdownBackgroundColor.WithAlpha(DropdownBackgroundColor.A * opacity));

                for (int i = 0; i < Options.Count; i++)
                {
                    float itemY = dropdownY + i * ItemHeight;
                    Vector2 itemPos = new Vector2(rect.X, itemY);
                    Vector2 itemSize = new Vector2(rect.Width, ItemHeight);

                    // 悬停/选中高亮
                    if (_hoveredIndex == i)
                        renderer.FillRect(itemPos, itemSize,
                            HoveredItemColor.WithAlpha(HoveredItemColor.A * opacity));
                    else if (i == SelectedIndex)
                        renderer.FillRect(itemPos, itemSize,
                            SelectedItemColor.WithAlpha(SelectedItemColor.A * opacity));

                    renderer.DrawText(itemPos, itemSize, Options[i],
                        "Default", FontSize, TextColor.WithAlpha(TextColor.A * opacity),
                        HAlign.Left, VAlign.Center, false, false);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Border - 边框控件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 边框控件 — 带背景和边框的容器。
    /// 相当于 UE Border / Unity Panel + Image。
    /// </summary>
    public class Border : WidgetBase
    {
        public Color BackgroundColor { get; set; } = new Color(0.15f, 0.16f, 0.19f, 0.9f);
        public string BackgroundTexture { get; set; } = string.Empty;
        public float CornerRadius { get; set; } = 3f;
        public float BorderThickness { get; set; } = 1f;
        public Color BorderColor { get; set; } = new Color(0.25f, 0.26f, 0.3f, 0.6f);

        public Border()
        {
            Name = "Border";
        }

        public override void ArrangeChildren()
        {
            float padX = Padding.Left + Padding.Right;
            float padY = Padding.Top + Padding.Bottom;
            float innerX = CachedRect.X + Padding.Left;
            float innerY = CachedRect.Y + Padding.Top;
            float innerW = Math.Max(0, Size.X - padX);
            float innerH = Math.Max(0, Size.Y - padY);

            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                child.Arrange(new Rectangle(innerX, innerY, innerW, innerH));
            }
        }

        protected internal override void OnPaint(PaintContext context, Rectangle rect)
        {
            var renderer = context.Renderer;
            float opacity = context.CurrentOpacity;

            // 背景
            if (!string.IsNullOrEmpty(BackgroundTexture))
                renderer.DrawTexture(rect.Position, rect.Size, BackgroundTexture,
                    new Vector4(0, 0, 1, 1), BackgroundColor.WithAlpha(BackgroundColor.A * opacity));
            else
                renderer.FillRoundedRect(rect.Position, rect.Size, CornerRadius,
                    BackgroundColor.WithAlpha(BackgroundColor.A * opacity));

            // 边框
            if (BorderThickness > 0)
                renderer.DrawRect(rect.Position, rect.Size, BorderThickness,
                    BorderColor.WithAlpha(BorderColor.A * opacity));

            // 绘制子控件
            foreach (var child in Children)
            {
                if (child.Visibility == EVisibility.Collapsed) continue;
                child.OnPaint(context, child.CachedRect);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // UserWidget - 用户自定义 Widget
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 用户自定义 Widget — 类似 UE UserWidget。
    /// 提供蓝图/代码自定义的基类，支持 Construct/Destruct 虚方法和绑定变量。
    /// </summary>
    public abstract class UserWidget : WidgetBase
    {
        /// <summary>构造时调用，类似于 UE 的 Construct。</summary>
        protected virtual void Construct() { }

        /// <summary>销毁时调用，类似于 UE 的 Destruct。</summary>
        protected virtual void Destruct() { }

        /// <summary>绑定变量字典。</summary>
        public Dictionary<string, object> BoundProperties { get; } = new();

        /// <summary>设置绑定属性的值并触发更新。</summary>
        protected void SetBoundProperty<T>(string name, T value)
        {
            BoundProperties[name] = value;
            OnPropertyChanged(name, value);
        }

        /// <summary>获取绑定属性的值。</summary>
        protected T GetBoundProperty<T>(string name, T defaultValue = default)
        {
            return BoundProperties.TryGetValue(name, out var v) ? (T)v : defaultValue;
        }

        /// <summary>属性变化时回调，子类重写以响应。</summary>
        protected virtual void OnPropertyChanged(string name, object value) { }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Construct();
        }

        protected override void OnDestroy()
        {
            Destruct();
            base.OnDestroy();
        }
    }
}
