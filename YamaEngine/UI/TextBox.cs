using System; using System.Collections.Generic; using System.Numerics; namespace YamaEngine.UI;
using YamaEngine.UI;

/// <summary>按键修饰符（兼容器旧控件）。</summary>
public enum Modifier
{
    None,
    Shift,
    Ctrl,
    Alt,
    Windows
}

/// <summary>剪贴板访问（引擎 UI 本地实现，避免依赖 WinForms）。</summary>
public static class YamaClipboard
{
    private static string _text = string.Empty;
    public static string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }
    public static void SetText(string text) => Text = text;
    public static string GetText() => Text;
}

public sealed class TextBox : WidgetBase
{
    public string Text = "", Placeholder = "Enter text...", FontName = "Default";
    public Vector4 TextColor = new(1,1,1,1), PlaceholderColor = new(0.5f,0.5f,0.5f,1), BgColor = new(0.15f,0.15f,0.15f,1), BorderColor = new(0.3f,0.3f,0.3f,1);
    public Vector4 FocusBorderColor = new(0.2f,0.5f,0.9f,1), SelectionColor = new(0.3f,0.6f,1,0.4f);
    public float FontSize = 14; public int MaxLength = 256;
    public bool Multiline, ReadOnly, Password, NumericOnly;
    public string AllowedChars = "";
    public bool HasFocus { get; private set; }
    public bool CanFocus = true;
    public event Action<TextBox, string>? OnTextChanged, OnSubmit;
    public event Action<TextBox>? OnFocusGained, OnFocusLost;

    private int _cursorPos, _selectionStart, _selectionEnd;
    private float _cursorTimer; private bool _cursorVisible;
    private string _visibleText = "";

    public TextBox() { Width = 200; Height = 28; CanFocus = true; }

    public string PasswordChar = "*";
    public string DisplayText => Password ? new string(PasswordChar[0], Text.Length) : Text;

    protected void OnDraw(CanvasRenderer cr)
    {
        cr.DrawRect(GlobalX, GlobalY, Width, Height, BgColor, 3);
        cr.DrawRectOutline(GlobalX, GlobalY, Width, Height, HasFocus ? FocusBorderColor : BorderColor, 1, 3);

        float padX = 8, padY = (Height - FontSize) * 0.5f;
        string disp = string.IsNullOrEmpty(Text) && !HasFocus ? "" : DisplayText;
        string drawText = string.IsNullOrEmpty(Text) && !HasFocus ? Placeholder : disp;
        var color = string.IsNullOrEmpty(Text) && !HasFocus ? PlaceholderColor : TextColor;

        Rectangle clip = new(GlobalX + padX, GlobalY + 2, Width - padX * 2, Height - 4);
        cr.PushClip(clip);
        cr.DrawText(drawText, GlobalX + padX, GlobalY + padY, color, FontSize);

        if (HasFocus)
        {
            _cursorTimer += 0.016f; if (_cursorTimer > 0.5f) { _cursorTimer = 0; _cursorVisible = !_cursorVisible; }
            if (_cursorVisible)
            {
                float cx = GlobalX + padX + (Text.Length > 0 ? Text.Length * FontSize * 0.5f : 0);
                cr.DrawRect(cx, GlobalY + padY, 1.5f, FontSize, TextColor);
            }
            if (_selectionStart != _selectionEnd)
            {
                float sx = GlobalX + padX + Math.Min(_selectionStart, _selectionEnd) * FontSize * 0.5f;
                float ex = GlobalX + padX + Math.Max(_selectionStart, _selectionEnd) * FontSize * 0.5f;
                cr.DrawRect(sx, GlobalY + padY, ex - sx, FontSize, SelectionColor);
            }
        }
        cr.PopClip();
    }

    protected bool OnMouseDown(float mx, float my, int button)
    {
        if (button == 0 && ContainsPoint(mx, my)) { SetFocus(); _cursorPos = Text.Length; return true; }
        return false;
    }

    protected bool OnKeyDown(Key key, char ch)
    {
        if (!HasFocus || ReadOnly) return false;
        if (key == Key.Backspace && _cursorPos > 0) { Text = Text.Remove(_cursorPos - 1, 1); _cursorPos--; OnTextChanged?.Invoke(this, Text); return true; }
        if (key == Key.Delete && _cursorPos < Text.Length) { Text = Text.Remove(_cursorPos, 1); OnTextChanged?.Invoke(this, Text); return true; }
        if (key == Key.Left && _cursorPos > 0) { _cursorPos--; return true; }
        if (key == Key.Right && _cursorPos < Text.Length) { _cursorPos++; return true; }
        if (key == Key.Home) { _cursorPos = 0; return true; }
        if (key == Key.End) { _cursorPos = Text.Length; return true; }
        if (key == Key.Enter) { if (!Multiline) { OnSubmit?.Invoke(this, Text); SetFocus(false); } else { Text = Text.Insert(_cursorPos, "\n"); _cursorPos++; OnTextChanged?.Invoke(this, Text); } return true; }
        if (key == Key.Escape) { SetFocus(false); return true; }
        if (key == Key.A && HasModifier(Modifier.Ctrl)) { _selectionStart = 0; _selectionEnd = Text.Length; return true; }
        if (key == Key.C && HasModifier(Modifier.Ctrl)) { CopyToClipboard(); return true; }
        if (key == Key.V && HasModifier(Modifier.Ctrl)) { PasteFromClipboard(); return true; }
        if (ch >= 32 && Text.Length < MaxLength)
        {
            if (NumericOnly && !char.IsDigit(ch) && ch != '.' && ch != '-') return true;
            if (!string.IsNullOrEmpty(AllowedChars) && !AllowedChars.Contains(ch)) return true;
            Text = Text.Insert(_cursorPos, ch.ToString()); _cursorPos++; OnTextChanged?.Invoke(this, Text);
            return true;
        }
        return false;
    }

    public void SetFocus(bool focused = true) { if (focused != HasFocus) { HasFocus = focused; if (focused) OnFocusGained?.Invoke(this); else OnFocusLost?.Invoke(this); } }

    public void SelectAll() { _selectionStart = 0; _selectionEnd = Text.Length; _cursorPos = Text.Length; }
    public void Clear() { Text = ""; _cursorPos = 0; OnTextChanged?.Invoke(this, Text); }

    private void CopyToClipboard() { if (_selectionStart != _selectionEnd) { int s=Math.Min(_selectionStart,_selectionEnd), e=Math.Max(_selectionStart,_selectionEnd); YamaClipboard.SetText(Text.Substring(s,e-s)); } }
    private void PasteFromClipboard() { try { string clip = YamaClipboard.GetText(); Text = Text.Insert(_cursorPos, clip); _cursorPos += clip.Length; OnTextChanged?.Invoke(this, Text); } catch { } }

    /// <summary>判断是否按下指定修饰键（旧控件简化实现）。</summary>
    private bool HasModifier(Modifier modifier) => false;
}
