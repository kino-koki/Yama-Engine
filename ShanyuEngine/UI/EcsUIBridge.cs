// ==================================================================================================
//  EcsUIBridge.cs — 山雨引擎 ECS ↔ UI 系统桥接器
// ==================================================================================================
//  命名空间: ShanyuEngine.UI
//  依赖:     ShanyuEngine.ECS
//
//  功能:
//    1. 遍历所有带 UICanvasComponent 的 ECS 实体
//    2. 为每个实体创建/同步 CanvasPanelWidget
//    3. 遍历 Canvas 子实体，为 UIElementComponent 创建对应 Widget
//    4. 同步 ECS 属性到 Widget (锚点、偏移、颜色、文本等)
//    5. 提供 WorldSpace Canvas 的世界变换支持
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;

namespace ShanyuEngine.UI
{
    /// <summary>
    /// ECS UI 系统桥接器。将 ECS 实体上的 <see cref="UICanvasComponent"/> 和 <see cref="UIElementComponent"/> 驱动为实际 UI Widget。
    /// </summary>
    public sealed class EcsUIBridge : IDisposable
    {
        private readonly EntityComponentSystem _ecs;
        private readonly Dictionary<int, CanvasPanelWidget> _canvases = new();
        private readonly Dictionary<int, Widget> _widgets = new();

        public event Action<int, string>? OnUIError;

        public EcsUIBridge(EntityComponentSystem ecs)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
        }

        public void Dispose()
        {
            _canvases.Clear();
            _widgets.Clear();
        }

        /// <summary>更新所有 ECS UI 实体。应在每帧渲染前调用。</summary>
        public void Update(float deltaTime, Vector2 screenSize)
        {
            foreach (int entityId in _ecs.AllEntities)
            {
                if (!_ecs.HasComponent<UICanvasComponent>(entityId))
                    continue;

                try
                {
                    UpdateCanvas(entityId, screenSize);
                }
                catch (Exception ex)
                {
                    OnUIError?.Invoke(entityId, ex.Message);
                }
            }
        }

        /// <summary>Tick 所有 Canvas 的 Widget 动画。应在每帧调用。</summary>
        public void Tick(float deltaTime)
        {
            foreach (var canvas in _canvases.Values)
            {
                if (canvas.IsVisible)
                    TickWidgetTree(canvas, deltaTime);
            }
        }

        private void TickWidgetTree(Widget widget, float dt)
        {
            widget.Tick(dt);
            if (widget is PanelWidget panel)
            {
                foreach (var child in panel.Children)
                    TickWidgetTree(child, dt);
            }
        }

        private void UpdateCanvas(int entityId, Vector2 screenSize)
        {
            var canvasComp = _ecs.GetComponent<UICanvasComponent>(entityId);
            if (!canvasComp.Enabled) return;

            // 获取或创建 CanvasPanelWidget
            if (!_canvases.TryGetValue(entityId, out var canvas))
            {
                canvas = new CanvasPanelWidget
                {
                    Name = $"Canvas_{entityId}",
                    DesiredSize = canvasComp.DesignSize,
                };
                _canvases[entityId] = canvas;
                canvasComp.RuntimeCanvas = canvas;
                _ecs.SetComponent(entityId, canvasComp);
            }

            // 同步 Canvas 尺寸和可见性
            canvas.IsVisible = canvasComp.Enabled;
            canvas.ZOrder = canvasComp.SortOrder;

            // 根据渲染模式设置 Canvas 尺寸
            switch (canvasComp.RenderMode)
            {
                case UICanvasRenderMode.ScreenSpaceOverlay:
                case UICanvasRenderMode.ScreenSpaceCamera:
                    canvas.DesiredSize = screenSize;
                    break;
                case UICanvasRenderMode.WorldSpace:
                    canvas.DesiredSize = canvasComp.DesignSize;
                    break;
            }

            // 同步子 UI 元素
            SyncCanvasChildren(entityId, canvas);

            // 执行布局
            canvas.Arrange(Vector2.Zero, canvas.DesiredSize, 1f);
        }

        private void SyncCanvasChildren(int canvasEntityId, CanvasPanelWidget canvas)
        {
            // 获取 Canvas 实体的子实体
            if (!_ecs.HasComponent<RelationshipComponent>(canvasEntityId))
                return;

            var rel = _ecs.GetComponent<RelationshipComponent>(canvasEntityId);
            if (rel.Children == null || rel.Children.Count == 0)
                return;

            var activeWidgets = new HashSet<Widget>();

            foreach (int childId in rel.Children)
            {
                if (!_ecs.HasComponent<UIElementComponent>(childId))
                    continue;

                var element = _ecs.GetComponent<UIElementComponent>(childId);

                // 获取或创建 Widget
                if (!_widgets.TryGetValue(childId, out var widget))
                {
                    widget = CreateWidgetFromComponent(element);
                    if (widget != null)
                    {
                        _widgets[childId] = widget;
                        canvas.AddChild(widget);
                    }
                }

                if (widget == null) continue;

                // 同步属性
                SyncElementToWidget(element, widget);
                activeWidgets.Add(widget);
            }

            // 清理已移除的子 Widget（简单实现：不处理，因为 ECS 实体移除需要单独处理）
        }

        private static Widget? CreateWidgetFromComponent(UIElementComponent element)
        {
            return element.ElementType switch
            {
                UIElementType.Text => new TextWidget(),
                UIElementType.Image => new ImageWidget(),
                UIElementType.Button => new ButtonWidget(),
                UIElementType.Slider => new SliderWidget(),
                UIElementType.CheckBox => new CheckBoxWidget(),
                UIElementType.InputField => new InputFieldWidget(),
                UIElementType.ProgressBar => new ProgressBarWidget(),
                UIElementType.Panel => new BorderWidget(),
                _ => null,
            };
        }

        private static void SyncElementToWidget(UIElementComponent element, Widget widget)
        {
            widget.Name = element.Name;
            widget.IsVisible = element.IsVisible;
            widget.IsEnabled = element.IsEnabled;
            widget.Anchors = new Anchors(element.AnchorMin.X, element.AnchorMin.Y, element.AnchorMax.X, element.AnchorMax.Y);
            widget.Offsets = new Margins(element.Offsets.X, element.Offsets.Y, element.Offsets.Z, element.Offsets.W);
            widget.Alignment = new Alignment(element.Alignment.X, element.Alignment.Y);
            widget.DesiredSize = element.DesiredSize;
            widget.RenderOpacity = element.Opacity;
            widget.ZOrder = element.ZOrder;

            switch (widget)
            {
                case TextWidget tw:
                    tw.Text = element.Text ?? "";
                    tw.Color = ToUiColor(element.TextColor);
                    tw.Font.Size = element.FontSize;
                    tw.Font.Bold = element.TextBold;
                    break;

                case ImageWidget iw:
                    iw.Brush.TintColor = ToUiColor(element.BackgroundColor);
                    if (!string.IsNullOrEmpty(element.TextureName))
                    {
                        iw.Brush.Type = BrushType.Texture;
                        iw.Brush.TextureName = element.TextureName;
                    }
                    break;

                case ButtonWidget bw:
                    bw.Text = element.Text ?? "";
                    bw.NormalBrush.TintColor = ToUiColor(element.BackgroundColor);
                    bw.HoveredBrush.TintColor = ToUiColor(element.HoverColor);
                    bw.PressedBrush.TintColor = ToUiColor(element.PressedColor);
                    bw.TextColor = ToUiColor(element.TextColor);
                    break;

                case SliderWidget sw:
                    sw.MinValue = element.MinValue;
                    sw.MaxValue = element.MaxValue;
                    sw.Value = element.CurrentValue;
                    sw.BarColor = ToUiColor(element.FillColor);
                    break;

                case CheckBoxWidget cw:
                    cw.IsChecked = element.IsChecked;
                    cw.Label = element.Text ?? "";
                    cw.LabelColor = ToUiColor(element.TextColor);
                    break;

                case InputFieldWidget ifw:
                    ifw.Placeholder = element.Placeholder ?? "";
                    ifw.IsPassword = element.IsPassword;
                    ifw.MaxLength = element.MaxLength;
                    ifw.TextColor = ToUiColor(element.TextColor);
                    break;

                case ProgressBarWidget pbw:
                    pbw.Percent = (element.CurrentValue - element.MinValue) / Math.Max(element.MaxValue - element.MinValue, 0.0001f);
                    pbw.BarColor = ToUiColor(element.FillColor);
                    break;

                case BorderWidget bdw:
                    bdw.Background.TintColor = ToUiColor(element.BackgroundColor);
                    break;
            }
        }

        private static Color ToUiColor(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

        /// <summary>获取指定 Canvas 实体对应的 CanvasPanelWidget（如果存在）。</summary>
        public CanvasPanelWidget? GetCanvas(int entityId)
        {
            _canvases.TryGetValue(entityId, out var canvas);
            return canvas;
        }

        /// <summary>获取所有活跃的 Canvas。</summary>
        public IEnumerable<CanvasPanelWidget> GetAllCanvases() => _canvases.Values;

        /// <summary>清理已销毁实体的 Widget。</summary>
        public void Cleanup()
        {
            var toRemove = new List<int>();
            foreach (var kv in _canvases)
            {
                if (!_ecs.HasEntity(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (var id in toRemove)
            {
                _canvases.Remove(id);
            }

            toRemove.Clear();
            foreach (var kv in _widgets)
            {
                if (!_ecs.HasEntity(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (var id in toRemove)
            {
                _widgets.Remove(id);
            }
        }
    }
}
