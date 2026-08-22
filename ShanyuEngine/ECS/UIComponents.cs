// ==================================================================================================
//  UIComponents.cs — 山雨引擎 ECS UI 系统组件
// ==================================================================================================
//  命名空间: ShanyuEngine.ECS
//  依赖:     ShanyuEngine.UI
//
//  功能:
//    ECS 版本的 UI 组件，由 EcsUIBridge 驱动实际的 UmgWidgetSystem。
// ==================================================================================================

using System;
using System.Numerics;
using ShanyuEngine.UI;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// UI 元素类型。
    /// </summary>
    public enum UIElementType
    {
        Canvas,
        Text,
        Image,
        Button,
        Slider,
        CheckBox,
        InputField,
        ProgressBar,
        Panel,
    }

    /// <summary>
    /// ECS UI Canvas 组件。附加到实体后，由 <see cref="EcsUIBridge"/> 创建 CanvasPanelWidget。
    /// </summary>
    public struct UICanvasComponent : IComponent
    {
        /// <summary>Canvas 是否启用。</summary>
        public bool Enabled { get; set; }

        /// <summary>Canvas 设计尺寸（像素）。</summary>
        public Vector2 DesignSize { get; set; }

        /// <summary>渲染模式：ScreenSpaceOverlay / ScreenSpaceCamera / WorldSpace。</summary>
        public UICanvasRenderMode RenderMode { get; set; }

        /// <summary>ScreenSpaceCamera 模式下的相机实体 ID（-1 表示主相机）。</summary>
        public int CameraEntityId { get; set; }

        /// <summary>WorldSpace 模式下的世界尺寸。</summary>
        public Vector2 WorldSize { get; set; }

        /// <summary>运行时 CanvasPanelWidget 引用（不序列化）。</summary>
        public CanvasPanelWidget? RuntimeCanvas { get; set; }

        /// <summary>是否接收输入事件。</summary>
        public bool ReceiveInput { get; set; }

        /// <summary>排序顺序（越高越靠前）。</summary>
        public int SortOrder { get; set; }

        public UICanvasComponent()
        {
            Enabled = true;
            DesignSize = new Vector2(1920, 1080);
            RenderMode = UICanvasRenderMode.ScreenSpaceOverlay;
            CameraEntityId = -1;
            WorldSize = new Vector2(1, 1);
            ReceiveInput = true;
            SortOrder = 0;
        }
    }

    /// <summary>
    /// Canvas 渲染模式。
    /// </summary>
    public enum UICanvasRenderMode
    {
        ScreenSpaceOverlay,
        ScreenSpaceCamera,
        WorldSpace,
    }

    /// <summary>
    /// ECS UI 元素组件。附加到 Canvas 子实体，由 <see cref="EcsUIBridge"/> 创建对应的 Widget。
    /// </summary>
    public struct UIElementComponent : IComponent
    {
        /// <summary>元素类型。</summary>
        public UIElementType ElementType { get; set; }

        /// <summary>元素名称（用于查找和绑定）。</summary>
        public string Name { get; set; }

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; }

        /// <summary>是否启用交互。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>锚点最小值 (0~1)。</summary>
        public Vector2 AnchorMin { get; set; }

        /// <summary>锚点最大值 (0~1)。</summary>
        public Vector2 AnchorMax { get; set; }

        /// <summary>偏移边距（像素）。</summary>
        public Vector4 Offsets { get; set; }

        /// <summary>对齐 (0~1)。</summary>
        public Vector2 Alignment { get; set; }

        /// <summary>期望尺寸（像素）。</summary>
        public Vector2 DesiredSize { get; set; }

        /// <summary>渲染透明度 (0~1)。</summary>
        public float Opacity { get; set; }

        /// <summary>Z 顺序。</summary>
        public int ZOrder { get; set; }

        // ── Text 专用 ──
        public string Text { get; set; }
        public Vector4 TextColor { get; set; }
        public float FontSize { get; set; }
        public bool TextBold { get; set; }

        // ── Image / Button / Panel 专用 ──
        public Vector4 BackgroundColor { get; set; }
        public string TextureName { get; set; }

        // ── Button 专用 ──
        public Vector4 HoverColor { get; set; }
        public Vector4 PressedColor { get; set; }
        public string OnClickEvent { get; set; }

        // ── Slider / ProgressBar 专用 ──
        public float MinValue { get; set; }
        public float MaxValue { get; set; }
        public float CurrentValue { get; set; }
        public Vector4 FillColor { get; set; }

        // ── CheckBox 专用 ──
        public bool IsChecked { get; set; }

        // ── InputField 专用 ──
        public string Placeholder { get; set; }
        public bool IsPassword { get; set; }
        public int MaxLength { get; set; }

        /// <summary>运行时 Widget 引用（不序列化）。</summary>
        public Widget? RuntimeWidget { get; set; }

        public UIElementComponent()
        {
            ElementType = UIElementType.Panel;
            Name = "UIElement";
            IsVisible = true;
            IsEnabled = true;
            AnchorMin = Vector2.Zero;
            AnchorMax = Vector2.Zero;
            Offsets = Vector4.Zero;
            Alignment = new Vector2(0.5f, 0.5f);
            DesiredSize = new Vector2(100, 32);
            Opacity = 1f;
            ZOrder = 0;
            Text = "";
            TextColor = new Vector4(1, 1, 1, 1);
            FontSize = 14f;
            BackgroundColor = new Vector4(0.2f, 0.22f, 0.27f, 1);
            TextureName = "";
            HoverColor = new Vector4(0.3f, 0.31f, 0.36f, 1);
            PressedColor = new Vector4(0.15f, 0.16f, 0.2f, 1);
            OnClickEvent = "";
            MinValue = 0f;
            MaxValue = 1f;
            CurrentValue = 0f;
            FillColor = new Vector4(0.26f, 0.59f, 0.98f, 1);
            IsChecked = false;
            Placeholder = "Enter text...";
            IsPassword = false;
            MaxLength = 100;
        }
    }
}
