using System; using System.Numerics; namespace ShanyuEngine.UI;


public sealed class Toggle : WidgetBase
{
    public bool IsOn; public string Label = "Toggle";
    public Vector4 OffColor = new(0.3f,0.3f,0.3f,1), OnColor = new(0.2f,0.6f,0.3f,1), ThumbColor = new(1,1,1,1), TextColor = new(1,1,1,1);
    public event Action<Toggle, bool>? OnToggled;

    public Toggle() { Width = 44; Height = 22; }

    protected override void OnDraw(CanvasRenderer cr)
    {
        cr.DrawPill(GlobalX, GlobalY, Width, Height, IsOn ? OnColor : OffColor);
        float thumbPos = IsOn ? GlobalX + Width - Height : GlobalX;
        cr.DrawCircle(new(thumbPos + Height*0.5f, GlobalY + Height*0.5f), Height*0.5f-3, ThumbColor);
        if (!string.IsNullOrEmpty(Label)) cr.DrawText(Label, GlobalX + Width + 8, GlobalY + Height*0.5f-6, TextColor, 12);
    }

    protected override bool OnMouseDown(float mx, float my, int button)
    {
        if (button == 0 && ContainsPoint(mx, my)) { IsOn = !IsOn; OnToggled?.Invoke(this, IsOn); return true; }
        return false;
    }
}

public sealed class Dropdown : WidgetBase
{
    public string[] Items = Array.Empty<string>(); public int SelectedIndex = -1;
    public Vector4 BgColor = new(0.15f,0.15f,0.15f,1), ItemColor = new(0.2f,0.2f,0.2f,1), HoverColor = new(0.3f,0.3f,0.3f,1), TextColor = new(1,1,1,1);
    public event Action<Dropdown, int>? OnItemSelected;
    private bool _open; private int _hoverIndex = -1;

    public string SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Length ? Items[SelectedIndex] : "";
    public Dropdown() { Width = 180; Height = 28; }

    protected override void OnDraw(CanvasRenderer cr)
    {
        cr.DrawRect(GlobalX, GlobalY, Width, Height, BgColor, 3);
        string txt = SelectedItem;
        cr.DrawText(string.IsNullOrEmpty(txt) ? "Select..." : txt, GlobalX + 8, GlobalY + Height*0.5f-7, string.IsNullOrEmpty(txt) ? new(0.5f,0.5f,0.5f,1) : TextColor, 14);
        cr.DrawText(_open ? "▲" : "▼", GlobalX + Width - 20, GlobalY + Height*0.5f-7, TextColor, 10);

        if (_open)
        {
            float itemH = 24; float listH = Items.Length * itemH;
            cr.DrawRect(GlobalX, GlobalY + Height, Width, listH, BgColor, 3);
            for (int i = 0; i < Items.Length; i++)
            {
                float iy = GlobalY + Height + i * itemH;
                if (i == _hoverIndex) cr.DrawRect(GlobalX, iy, Width, itemH, HoverColor, 0);
                if (i == SelectedIndex) cr.DrawRect(GlobalX + 2, iy + 2, 2, itemH - 4, new(0.2f,0.5f,0.9f,1));
                cr.DrawText(Items[i], GlobalX + 8, iy + itemH * 0.5f - 7, TextColor, 13);
            }
        }
    }

    protected override bool OnMouseDown(float mx, float my, int button)
    {
        if (button == 0 && ContainsPoint(mx, my)) { _open = !_open; return true; }
        if (_open)
        {
            float itemH = 24;
            for (int i = 0; i < Items.Length; i++)
            {
                float iy = GlobalY + Height + i * itemH;
                if (mx >= GlobalX && mx <= GlobalX + Width && my >= iy && my <= iy + itemH)
                { SelectedIndex = i; _open = false; OnItemSelected?.Invoke(this, i); return true; }
            }
            if (my < GlobalY || my > GlobalY + Height + Items.Length * itemH) _open = false;
        }
        return false;
    }

    protected override bool OnMouseMove(float mx, float my)
    {
        if (_open)
        {
            float itemH = 24; _hoverIndex = -1;
            for (int i = 0; i < Items.Length; i++)
            { float iy = GlobalY + Height + i * itemH; if (mx >= GlobalX && mx <= GlobalX + Width && my >= iy && my <= iy + itemH) { _hoverIndex = i; break; } }
        }
        return false;
    }
}
