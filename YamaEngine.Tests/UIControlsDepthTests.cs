using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Input;
using YamaEngine.UI;
using UI_ButtonState = YamaEngine.UI.ButtonState;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>
/// Unity5 uGUI 控件交互层深度验证（Button/Image/TextBlock/Slider/CheckBox/ComboBox/ProgressBar/EditableTextBox）。
/// 对应 Unity 5 的 UnityEngine.UI 控件事件/状态机：
///   * Button click 状态机（ClickMethod.Up/Down，hover/press 状态切换，禁用忽略点击）
///   * Slider 拖拽 + StepSize + 滚轮
///   * CheckBox 三态切换（Toggle）
///   * ComboBox 下拉选择（Dropdown）
///   * ProgressBar 平滑动画（对应 fillAmount 动画）
///   * TextBlock 内容测量 + AutoWrap
///   * EditableTextBox 输入管线（caret/Backspace/maxLength）
/// 全部 CPU 级确定性，走 WidgetSystem 真实分发路径（HitTest/DispatchMouse/DispatchKey）。
/// </summary>
public static class UIControlsDepthTests
{
    // ── 辅助：近似相等 ──
    private static void AssertNear(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    // ── 辅助：搭建独立 WidgetSystem（避免污染其他测试的单例） ──
    private static WidgetSystem MakeSystem()
    {
        WidgetSystem.ResetInstance();
        var renderer = new CanvasRenderer();
        var sys = new WidgetSystem(renderer);
        sys.Initialize(1920, 1080);
        return sys;
    }

    // ── 辅助：把 widget 挂到 canvas 并布置到给定矩形 ──
    private static void Place(WidgetSystem sys, WidgetBase w, float x, float y, float w2, float h2)
    {
        sys.AddToViewport(w);
        w.Arrange(new Rectangle(x, y, w2, h2));
    }

    /// <summary>ClickMethod.Up（默认）：按下不触发，抬起触发一次。</summary>
    public static void TestButtonClickUpDefault()
    {
        var sys = MakeSystem();
        var btn = new Button("OK") { ClickMethod = ClickMethod.Up };
        Place(sys, btn, 100, 100, 200, 50);

        int clicks = 0;
        btn.OnClicked += _ => clicks++;

        // 按下：不触发
        sys.DispatchMouseDown(MouseButton.Left, new Vector2(150, 125));
        AssertEqual(0, clicks, "ClickMethod.Up 按下不应触发 click。");
        AssertTrue(btn.IsPressed, "按下后 IsPressed 应为 true。");
        AssertEqual(UI_ButtonState.Pressed, btn.CurrentState, "按下后状态应为 Pressed。");

        // 抬起（仍在按钮内）：触发一次
        sys.DispatchMouseUp(MouseButton.Left, new Vector2(150, 125));
        AssertEqual(1, clicks, "ClickMethod.Up 抬起应触发一次 click。");
        AssertTrue(!btn.IsPressed, "抬起后 IsPressed 应为 false。");
    }

    /// <summary>ClickMethod.Down：按下立即触发，抬起不再触发。</summary>
    public static void TestButtonClickDownTrigger()
    {
        var sys = MakeSystem();
        var btn = new Button("OK") { ClickMethod = ClickMethod.Down };
        Place(sys, btn, 100, 100, 200, 50);

        int clicks = 0;
        btn.OnClicked += _ => clicks++;

        sys.DispatchMouseDown(MouseButton.Left, new Vector2(150, 125));
        AssertEqual(1, clicks, "ClickMethod.Down 按下应立即触发一次 click。");

        sys.DispatchMouseUp(MouseButton.Left, new Vector2(150, 125));
        AssertEqual(1, clicks, "ClickMethod.Down 抬起不应再触发 click。");
    }

    /// <summary>Hover 状态机：进入悬停置 Hovered，离开回 Normal。</summary>
    public static void TestButtonHoverStateTransition()
    {
        var sys = MakeSystem();
        var btn = new Button("OK");
        Place(sys, btn, 100, 100, 200, 50);

        // 移入按钮中心
        sys.DispatchMouseMove(new Vector2(150, 125));
        AssertTrue(btn.IsHovered, "鼠标进入按钮应 IsHovered=true。");
        AssertEqual(UI_ButtonState.Hovered, btn.CurrentState, "进入后状态应为 Hovered。");

        // 移出按钮
        sys.DispatchMouseMove(new Vector2(500, 500));
        AssertTrue(!btn.IsHovered, "鼠标离开按钮应 IsHovered=false。");
        AssertEqual(UI_ButtonState.Normal, btn.CurrentState, "离开后状态应回 Normal。");
    }

    /// <summary>禁用按钮：HitTest 过滤，鼠标事件不触达。</summary>
    public static void TestButtonDisabledIgnoresClick()
    {
        var sys = MakeSystem();
        var btn = new Button("OK") { IsEnabled = false };
        Place(sys, btn, 100, 100, 200, 50);

        int clicks = 0, pressed = 0;
        btn.OnClicked += _ => clicks++;
        btn.OnPressed += _ => pressed++;

        sys.DispatchMouseDown(MouseButton.Left, new Vector2(150, 125));
        sys.DispatchMouseUp(MouseButton.Left, new Vector2(150, 125));
        AssertEqual(0, clicks, "禁用按钮不应触发 click。");
        AssertEqual(0, pressed, "禁用按钮不应触发 press。");
        AssertEqual(UI_ButtonState.Normal, btn.CurrentState, "禁用按钮状态应保持 Normal（未被交互）。");
    }

    /// <summary>非左键点击被忽略。</summary>
    public static void TestButtonNonLeftIgnored()
    {
        var sys = MakeSystem();
        var btn = new Button("OK");
        Place(sys, btn, 100, 100, 200, 50);

        int clicks = 0;
        btn.OnClicked += _ => clicks++;

        sys.DispatchMouseDown(MouseButton.Right, new Vector2(150, 125));
        sys.DispatchMouseUp(MouseButton.Right, new Vector2(150, 125));
        AssertEqual(0, clicks, "右键点击不应触发 click。");
        AssertTrue(!btn.IsPressed, "右键点击不应置 IsPressed。");
    }

    /// <summary>Slider 拖拽：down/move/up 更新 Value。</summary>
    public static void TestSliderDragUpdatesValue()
    {
        var sys = MakeSystem();
        var slider = new Slider { MinValue = 0, MaxValue = 1, Value = 0 };
        Place(sys, slider, 0, 0, 200, 20);

        float lastValue = -1;
        slider.OnValueChanged += v => lastValue = v;

        // 点击中点 → t=0.5
        sys.DispatchMouseDown(MouseButton.Left, new Vector2(100, 10));
        AssertNear(slider.Value, 0.5f, 0.001f, "点击中点 Value 应为 0.5。");
        AssertNear(lastValue, 0.5f, 0.001f, "OnValueChanged 应收到 0.5。");

        // 拖到 t=0.25
        sys.DispatchMouseMove(new Vector2(50, 10));
        AssertNear(slider.Value, 0.25f, 0.001f, "拖到 25% 位置 Value 应为 0.25。");

        // 抬起结束拖拽
        sys.DispatchMouseUp(MouseButton.Left, new Vector2(50, 10));
    }

    /// <summary>Slider StepSize：value 落到 step 的整数倍。</summary>
    public static void TestSliderStepSize()
    {
        var sys = MakeSystem();
        var slider = new Slider { MinValue = 0, MaxValue = 1, Value = 0, StepSize = 0.25f };
        Place(sys, slider, 0, 0, 200, 20);

        sys.DispatchMouseDown(MouseButton.Left, new Vector2(90, 10)); // raw≈0.45 → 0.5
        AssertNear(slider.Value, 0.5f, 0.001f, "StepSize=0.25 时 raw=0.45 应 round 到 0.5。");
    }

    /// <summary>Slider 滚轮：delta>0 增、delta<0 减，clamp 到 [Min,Max]。</summary>
    public static void TestSliderScroll()
    {
        var sys = MakeSystem();
        var slider = new Slider { MinValue = 0, MaxValue = 1, Value = 0.5f };
        Place(sys, slider, 0, 0, 200, 20);

        sys.DispatchMouseWheel(new Vector2(100, 10), 1f);
        AssertTrue(slider.Value > 0.5f, "滚轮正 delta 应增大 Value。");

        sys.DispatchMouseWheel(new Vector2(100, 10), -1f);
        sys.DispatchMouseWheel(new Vector2(100, 10), -1f);
        AssertTrue(slider.Value < 0.5f, "连续负 delta 应减小 Value。");

        // 连续减到下界
        for (int i = 0; i < 20; i++)
            sys.DispatchMouseWheel(new Vector2(100, 10), -1f);
        AssertNear(slider.Value, 0f, 0.001f, "连续负 delta 应 clamp 到 MinValue=0。");
    }

    /// <summary>CheckBox 二态切换：Unchecked ↔ Checked。</summary>
    public static void TestCheckBoxToggle()
    {
        var cb = new CheckBox();
        AssertEqual(CheckBoxState.Unchecked, cb.State, "默认应为 Unchecked。");

        int changes = 0;
        CheckBoxState lastState = CheckBoxState.Unchecked;
        cb.OnCheckStateChanged += s => { changes++; lastState = s; };

        cb.OnClick(Vector2.Zero, MouseButton.Left);
        AssertEqual(CheckBoxState.Checked, cb.State, "首次点击应为 Checked。");
        AssertEqual(1, changes, "首次点击应触发 1 次 OnCheckStateChanged。");
        AssertEqual(CheckBoxState.Checked, lastState, "回调应收到 Checked。");
        AssertTrue(cb.IsChecked, "IsChecked 应为 true。");

        cb.OnClick(Vector2.Zero, MouseButton.Left);
        AssertEqual(CheckBoxState.Unchecked, cb.State, "再次点击应回 Unchecked。");
        AssertEqual(2, changes, "第二次点击应再触发一次。");
        AssertTrue(!cb.IsChecked, "IsChecked 应为 false。");
    }

    /// <summary>CheckBox 三态循环：Unchecked → Checked → Indeterminate → Unchecked。</summary>
    public static void TestCheckBoxThreeState()
    {
        var cb = new CheckBox { IsThreeState = true };
        AssertEqual(CheckBoxState.Unchecked, cb.State, "默认 Unchecked。");

        cb.OnClick(Vector2.Zero, MouseButton.Left);
        AssertEqual(CheckBoxState.Checked, cb.State, "三态第一次应为 Checked。");

        cb.OnClick(Vector2.Zero, MouseButton.Left);
        AssertEqual(CheckBoxState.Indeterminate, cb.State, "三态第二次应为 Indeterminate。");

        cb.OnClick(Vector2.Zero, MouseButton.Left);
        AssertEqual(CheckBoxState.Unchecked, cb.State, "三态第三次应回 Unchecked。");
    }

    /// <summary>ComboBox 打开下拉并选择选项。</summary>
    public static void TestComboBoxSelection()
    {
        var combo = new ComboBox();
        combo.Options.Add("A");
        combo.Options.Add("B");
        combo.Options.Add("C");
        combo.Arrange(new Rectangle(0, 0, 160, 24));

        AssertEqual(-1, combo.SelectedIndex, "默认未选。");
        AssertTrue(!combo.IsOpen, "默认收起。");

        int selChanged = 0;
        combo.OnSelectionChanged += (idx, item) => selChanged++;

        // 第一次点击：打开
        combo.OnMouseDown(new Vector2(80, 12), MouseButton.Left);
        AssertTrue(combo.IsOpen, "第一次点击应打开下拉。");

        // 点击 dropdown 区域第 2 项（localY=36 → itemY=12 → idx=0? 看：dropdownY=CachedRect.Bottom=24；itemY=localPos.Y-(dropdownY-CachedRect.Y)=36-24=12；idx=12/24=0）
        combo.OnMouseDown(new Vector2(80, 36), MouseButton.Left);
        AssertTrue(!combo.IsOpen, "选择后应关闭下拉。");
        AssertEqual(0, combo.SelectedIndex, "点击 dropdown 第 1 行应选中 idx=0。");
        AssertEqual("A", combo.SelectedItem, "SelectedItem 应为 A。");
        AssertEqual(1, selChanged, "应触发 1 次 OnSelectionChanged。");

        // 再开并选第 2 行
        combo.OnMouseDown(new Vector2(80, 12), MouseButton.Left);
        combo.OnMouseDown(new Vector2(80, 60), MouseButton.Left); // itemY=60-24=36；idx=36/24=1
        AssertEqual(1, combo.SelectedIndex, "点击 dropdown 第 2 行应选中 idx=1。");
        AssertEqual("B", combo.SelectedItem, "SelectedItem 应为 B。");
    }

    /// <summary>ProgressBar Tick 平滑动画：displayPercent 逐渐趋近 Percent。</summary>
    public static void TestProgressBarAnimation()
    {
        var pb = new ProgressBar { Min = 0, Max = 1, Percent = 1f, AnimSpeed = 5f };
        // 内部 _displayPercent 初值 = Percent 构造时的 0.5（ProgressBar 默认 Percent=0.5）
        // 构造后再改 Percent=1，Tick 会向 1 平滑趋近
        pb.Tick(0.1f);
        // AnimSpeed=5，dt=0.1 → 步长 min(|1-0.5|, 0.5)=0.5 → _displayPercent=1
        AssertNear(pb.Percent, 1f, 0.001f, "Percent 目标为 1。");
        // 再次 Tick 不再变化（已收敛）
        pb.Tick(0.1f);
        AssertNear(pb.Percent, 1f, 0.001f, "Percent 不变。");

        // AnimSpeed 很小：逐步趋近
        var pb2 = new ProgressBar { Min = 0, Max = 1, Percent = 0f, AnimSpeed = 1f };
        // 构造时 _displayPercent=0.5；Percent 改成 0 → Tick 朝 0 走
        pb2.Tick(0.1f); // 步长 min(0.5, 0.1)=0.1 → display=0.4
        AssertTrue(pb2.Percent < 0.5f, "目标 Percent=0，Tick 后 display 应朝 0 走（<0.5）。");
    }

    /// <summary>TextBlock 内容测量：单行 + AutoWrap。</summary>
    public static void TestTextBlockDesiredSize()
    {
        var tb = new TextBlock("Hello");
        tb.FontSize = 10f;
        tb.LineSpacing = 1f;
        tb.AutoWrap = false;

        var size = tb.ComputeDesiredSize(new Vector2(0, 0));
        // 5 chars * 10 * 0.6 = 30 宽，10 * 1 = 10 高
        AssertNear(size.X, 30f, 0.01f, "单行宽度 = len * fontSize * 0.6。");
        AssertNear(size.Y, 10f, 0.01f, "单行高度 = fontSize * lineSpacing。");

        // AutoWrap：宽 20，单字宽 6 → 每行 floor(20/6)=3 字符 → 5 字符需 2 行
        tb.AutoWrap = true;
        var size2 = tb.ComputeDesiredSize(new Vector2(20, 0));
        AssertNear(size2.X, 20f, 0.01f, "AutoWrap 宽度 = 可用宽。");
        AssertNear(size2.Y, 20f, 0.01f, "AutoWrap 2 行 = 2 * lineHeight。");
    }

    /// <summary>EditableTextBox 输入管线：focus + text input + Backspace。</summary>
    public static void TestEditableTextBoxTextInput()
    {
        var sys = MakeSystem();
        var box = new EditableTextBox();
        Place(sys, box, 100, 100, 200, 30);

        int textChanged = 0;
        box.OnTextChanged += _ => textChanged++;

        // 点击获取焦点
        sys.DispatchMouseDown(MouseButton.Left, new Vector2(150, 115));
        AssertTrue(sys.FocusedWidget == box, "点击文本框应获得焦点。");

        // 输入 'H' 'i'
        sys.DispatchTextInput('H');
        sys.DispatchTextInput('i');
        AssertEqual("Hi", box.Text, "输入 H/i 后 Text 应为 Hi。");
        AssertEqual(2, textChanged, "两次输入各触发一次 OnTextChanged。");
        AssertEqual(2, box.CursorIndex, "光标应在末尾。");

        // Backspace 删掉 'i'
        sys.DispatchKeyDown(InputKey.Backspace);
        AssertEqual("H", box.Text, "Backspace 应删掉末尾字符。");
        AssertEqual(3, textChanged, "Backspace 触发 OnTextChanged。");
        AssertEqual(1, box.CursorIndex, "光标回退一位。");
    }

    /// <summary>EditableTextBox MaxLength 截断输入。</summary>
    public static void TestEditableTextBoxMaxLength()
    {
        var sys = MakeSystem();
        var box = new EditableTextBox { MaxLength = 3 };
        Place(sys, box, 100, 100, 200, 30);

        sys.DispatchMouseDown(MouseButton.Left, new Vector2(150, 115));
        foreach (char c in "Hello")
            sys.DispatchTextInput(c);
        AssertEqual("Hel", box.Text, "MaxLength=3 应截断到 3 字符。");
    }

    /// <summary>Button desired size 综合文本与图片。</summary>
    public static void TestButtonDesiredSize()
    {
        var btn = new Button("Hi") { FontSize = 10f, ImageTexture = "icon", ImageSize = new Vector2(16, 16), ImageTextSpacing = 4 };
        var size = btn.ComputeDesiredSize(new Vector2(0, 0));
        // textW=2*10*0.6=12；imgW=16+4=20；总=12+20+24=56；高=max(10*1.6,16)+12=28
        AssertNear(size.X, 56f, 0.01f, "文本+图片+padding 宽度。");
        AssertNear(size.Y, 28f, 0.01f, "高 = max(textLineHeight, imageH) + padding。");
    }

    /// <summary>Image TintColor + NineSlice 切换（基础 setter）。</summary>
    public static void TestImageTintColorAndStretch()
    {
        var img = new Image();
        img.TextureName = "tex";
        img.TintColor = new Color(1f, 0.5f, 0.25f, 1f);
        img.Stretch = ImageStretch.Uniform;
        img.NineSliceMargin = 4f;
        AssertEqual("tex", img.TextureName, "TextureName 赋值。");
        AssertNear(img.TintColor.G, 0.5f, 0.001f, "TintColor.G 赋值。");
        AssertEqual(ImageStretch.Uniform, img.Stretch, "Stretch 赋值。");
        AssertNear(img.NineSliceMargin, 4f, 0.001f, "NineSliceMargin 赋值。");
    }
}
