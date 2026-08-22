// ==================================================================================================
//  UIEditorPanel.cs — 山雨引擎 UI 编辑器面板
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ImGuiNET, ShanyuEngine.ECS, ShanyuEngine.UI
//
//  功能:
//    * 显示并编辑选中实体的 UICanvasComponent / UIElementComponent
//    * Canvas 属性: 设计尺寸、渲染模式、排序
//    * UI 元素属性: 类型、锚点、偏移、对齐、颜色、文本
//    * 添加/删除 UI 元素
//    * Canvas 子元素层级树
// ==================================================================================================

using System;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.ECS;
using ShanyuEngine.UI;

namespace ShanyuEngine.Editor.Panels
{
    /// <summary>
    /// UI 系统编辑器面板。提供 Canvas 和 UI 元素的可视化编辑。
    /// </summary>
    public sealed class UIEditorPanel
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;

        // ── UI 状态 ────────────────────────────────────────────────────────────────

        private readonly Vector4 _headerColor = new(0.9f, 0.5f, 0.2f, 1f);
        private int _addElementTypeIndex;

        // ── 事件 ──────────────────────────────────────────────────────────────────

        public event Action? OnSceneModified;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        public string Title { get; set; } = "UI 编辑器";
        public bool IsVisible { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public UIEditorPanel(EntityComponentSystem ecs, EcsSelectionState selection)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  渲染
        // ─────────────────────────────────────────────────────────────────────────

        public void Render()
        {
            if (!IsVisible) return;

            ImGui.SetNextWindowSizeConstraints(new Vector2(340, 450), new Vector2(float.MaxValue, float.MaxValue));
            if (!ImGui.Begin($"{Title}###UIEditor"))
            {
                ImGui.End();
                return;
            }

            int entityId = _selection.Active;

            if (entityId < 0 || !_ecs.HasEntity(entityId))
            {
                ImGui.TextDisabled("未选中任何实体");
                ImGui.TextDisabled("选中带 Canvas 或 UI 元素的实体以编辑。");
                ImGui.End();
                return;
            }

            bool hasCanvas = _ecs.HasComponent<UICanvasComponent>(entityId);
            bool hasElement = _ecs.HasComponent<UIElementComponent>(entityId);

            if (!hasCanvas && !hasElement)
            {
                ImGui.TextDisabled($"实体 #{entityId} 没有 UI 组件");
                if (ImGui.Button("添加 Canvas"))
                {
                    _ecs.AddComponent(entityId, new UICanvasComponent());
                    OnSceneModified?.Invoke();
                }
                ImGui.SameLine();
                if (ImGui.Button("添加 UI 元素"))
                {
                    _ecs.AddComponent(entityId, new UIElementComponent());
                    OnSceneModified?.Invoke();
                }
                ImGui.End();
                return;
            }

            string name = _ecs.GetEntityName(entityId) ?? "Entity";
            ImGui.TextColored(_headerColor, $"{name} #{entityId}");
            ImGui.Separator();

            if (hasCanvas)
            {
                DrawCanvasEditor(entityId);
            }

            if (hasElement)
            {
                DrawElementEditor(entityId);
            }

            // 如果当前实体是 Canvas，显示子元素层级
            if (hasCanvas)
            {
                DrawCanvasChildrenTree(entityId);
            }

            ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Canvas 编辑
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawCanvasEditor(int entityId)
        {
            if (!ImGui.CollapsingHeader("Canvas", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            var canvas = _ecs.GetComponent<UICanvasComponent>(entityId);
            bool changed = false;

            changed |= ImGui.Checkbox("启用", ref canvas.Enabled);

            Vector2 designSize = canvas.DesignSize;
            if (ImGui.DragFloat2("设计尺寸", ref designSize, 1f, 1f, 8192f))
            {
                canvas.DesignSize = designSize;
                changed = true;
            }

            var modes = new[] { "ScreenSpaceOverlay", "ScreenSpaceCamera", "WorldSpace" };
            int modeIdx = (int)canvas.RenderMode;
            if (ImGui.Combo("渲染模式", ref modeIdx, modes, modes.Length))
            {
                canvas.RenderMode = (UICanvasRenderMode)modeIdx;
                changed = true;
            }

            if (canvas.RenderMode == UICanvasRenderMode.ScreenSpaceCamera)
            {
                int camId = canvas.CameraEntityId;
                if (ImGui.DragInt("相机实体 ID", ref camId, 1, -1, 100000))
                {
                    canvas.CameraEntityId = camId;
                    changed = true;
                }
            }

            if (canvas.RenderMode == UICanvasRenderMode.WorldSpace)
            {
                Vector2 worldSize = canvas.WorldSize;
                if (ImGui.DragFloat2("世界尺寸", ref worldSize, 0.01f, 0.001f, 100f))
                {
                    canvas.WorldSize = worldSize;
                    changed = true;
                }
            }

            changed |= ImGui.Checkbox("接收输入", ref canvas.ReceiveInput);

            int sortOrder = canvas.SortOrder;
            if (ImGui.DragInt("排序顺序", ref sortOrder, 1, -100, 100))
            {
                canvas.SortOrder = sortOrder;
                changed = true;
            }

            if (changed)
            {
                _ecs.SetComponent(entityId, canvas);
                OnSceneModified?.Invoke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  UI 元素编辑
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawElementEditor(int entityId)
        {
            if (!ImGui.CollapsingHeader("UI 元素", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            var element = _ecs.GetComponent<UIElementComponent>(entityId);
            bool changed = false;

            // 基础属性
            var types = new[] { "Canvas", "Text", "Image", "Button", "Slider", "CheckBox", "InputField", "ProgressBar", "Panel" };
            int typeIdx = (int)element.ElementType;
            if (ImGui.Combo("类型", ref typeIdx, types, types.Length))
            {
                element.ElementType = (UIElementType)typeIdx;
                changed = true;
            }

            string name = element.Name ?? "";
            if (ImGui.InputText("名称", ref name, 64))
            {
                element.Name = name;
                changed = true;
            }

            changed |= ImGui.Checkbox("可见", ref element.IsVisible);
            changed |= ImGui.Checkbox("启用交互", ref element.IsEnabled);

            // 布局属性
            ImGui.Separator();
            ImGui.Text("布局");

            Vector2 anchorMin = element.AnchorMin;
            if (ImGui.DragFloat2("锚点最小", ref anchorMin, 0.01f, 0f, 1f))
            {
                element.AnchorMin = anchorMin;
                changed = true;
            }

            Vector2 anchorMax = element.AnchorMax;
            if (ImGui.DragFloat2("锚点最大", ref anchorMax, 0.01f, 0f, 1f))
            {
                element.AnchorMax = anchorMax;
                changed = true;
            }

            Vector4 offsets = element.Offsets;
            if (ImGui.DragFloat4("偏移 (左/上/右/下)", ref offsets, 0.5f, -1000f, 1000f))
            {
                element.Offsets = offsets;
                changed = true;
            }

            Vector2 alignment = element.Alignment;
            if (ImGui.DragFloat2("对齐", ref alignment, 0.01f, 0f, 1f))
            {
                element.Alignment = alignment;
                changed = true;
            }

            Vector2 desiredSize = element.DesiredSize;
            if (ImGui.DragFloat2("期望尺寸", ref desiredSize, 0.5f, 0f, 4096f))
            {
                element.DesiredSize = desiredSize;
                changed = true;
            }

            float opacity = element.Opacity;
            if (ImGui.SliderFloat("透明度", ref opacity, 0f, 1f))
            {
                element.Opacity = opacity;
                changed = true;
            }

            int zOrder = element.ZOrder;
            if (ImGui.DragInt("Z 顺序", ref zOrder, 1, -100, 100))
            {
                element.ZOrder = zOrder;
                changed = true;
            }

            // 类型专用属性
            changed |= DrawElementTypeProperties(ref element);

            if (changed)
            {
                _ecs.SetComponent(entityId, element);
                OnSceneModified?.Invoke();
            }

            ImGui.Separator();
            if (ImGui.Button("删除 UI 元素"))
            {
                _ecs.RemoveComponent<UIElementComponent>(entityId);
                OnSceneModified?.Invoke();
            }
        }

        private bool DrawElementTypeProperties(ref UIElementComponent element)
        {
            bool changed = false;

            switch (element.ElementType)
            {
                case UIElementType.Text:
                    ImGui.Separator();
                    ImGui.Text("文本属性");
                    string text = element.Text ?? "";
                    if (ImGui.InputTextMultiline("文本", ref text, 256, new Vector2(0, 60)))
                    {
                        element.Text = text;
                        changed = true;
                    }
                    Vector4 textColor = element.TextColor;
                    if (ImGui.ColorEdit4("文本颜色", ref textColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.TextColor = textColor;
                        changed = true;
                    }
                    float fontSize = element.FontSize;
                    if (ImGui.DragFloat("字号", ref fontSize, 0.5f, 4f, 128f))
                    {
                        element.FontSize = fontSize;
                        changed = true;
                    }
                    changed |= ImGui.Checkbox("粗体", ref element.TextBold);
                    break;

                case UIElementType.Image:
                case UIElementType.Panel:
                    ImGui.Separator();
                    ImGui.Text(element.ElementType == UIElementType.Image ? "图片属性" : "面板属性");
                    Vector4 bgColor = element.BackgroundColor;
                    if (ImGui.ColorEdit4("背景颜色", ref bgColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.BackgroundColor = bgColor;
                        changed = true;
                    }
                    string texName = element.TextureName ?? "";
                    if (ImGui.InputText("纹理名称", ref texName, 128))
                    {
                        element.TextureName = texName;
                        changed = true;
                    }
                    break;

                case UIElementType.Button:
                    ImGui.Separator();
                    ImGui.Text("按钮属性");
                    string btnText = element.Text ?? "";
                    if (ImGui.InputText("按钮文本", ref btnText, 128))
                    {
                        element.Text = btnText;
                        changed = true;
                    }
                    Vector4 btnBg = element.BackgroundColor;
                    if (ImGui.ColorEdit4("背景色", ref btnBg, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.BackgroundColor = btnBg;
                        changed = true;
                    }
                    Vector4 hoverColor = element.HoverColor;
                    if (ImGui.ColorEdit4("悬停色", ref hoverColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.HoverColor = hoverColor;
                        changed = true;
                    }
                    Vector4 pressedColor = element.PressedColor;
                    if (ImGui.ColorEdit4("按下色", ref pressedColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.PressedColor = pressedColor;
                        changed = true;
                    }
                    break;

                case UIElementType.Slider:
                case UIElementType.ProgressBar:
                    ImGui.Separator();
                    ImGui.Text(element.ElementType == UIElementType.Slider ? "滑块属性" : "进度条属性");
                    float minVal = element.MinValue;
                    if (ImGui.DragFloat("最小值", ref minVal, 0.1f, -100000f, 100000f))
                    {
                        element.MinValue = minVal;
                        changed = true;
                    }
                    float maxVal = element.MaxValue;
                    if (ImGui.DragFloat("最大值", ref maxVal, 0.1f, -100000f, 100000f))
                    {
                        element.MaxValue = maxVal;
                        changed = true;
                    }
                    float curVal = element.CurrentValue;
                    if (ImGui.SliderFloat("当前值", ref curVal, element.MinValue, element.MaxValue))
                    {
                        element.CurrentValue = curVal;
                        changed = true;
                    }
                    Vector4 fillColor = element.FillColor;
                    if (ImGui.ColorEdit4("填充颜色", ref fillColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                    {
                        element.FillColor = fillColor;
                        changed = true;
                    }
                    break;

                case UIElementType.CheckBox:
                    ImGui.Separator();
                    ImGui.Text("复选框属性");
                    changed |= ImGui.Checkbox("已勾选", ref element.IsChecked);
                    string cbLabel = element.Text ?? "";
                    if (ImGui.InputText("标签", ref cbLabel, 128))
                    {
                        element.Text = cbLabel;
                        changed = true;
                    }
                    break;

                case UIElementType.InputField:
                    ImGui.Separator();
                    ImGui.Text("输入框属性");
                    string placeholder = element.Placeholder ?? "";
                    if (ImGui.InputText("占位符", ref placeholder, 128))
                    {
                        element.Placeholder = placeholder;
                        changed = true;
                    }
                    changed |= ImGui.Checkbox("密码模式", ref element.IsPassword);
                    int maxLen = element.MaxLength;
                    if (ImGui.DragInt("最大长度", ref maxLen, 1, 1, 10000))
                    {
                        element.MaxLength = maxLen;
                        changed = true;
                    }
                    break;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Canvas 子元素树
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawCanvasChildrenTree(int canvasEntityId)
        {
            if (!ImGui.CollapsingHeader("子元素层级", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (!_ecs.HasComponent<RelationshipComponent>(canvasEntityId))
            {
                ImGui.TextDisabled("Canvas 没有子实体");
                return;
            }

            var rel = _ecs.GetComponent<RelationshipComponent>(canvasEntityId);
            if (rel.Children == null || rel.Children.Count == 0)
            {
                ImGui.TextDisabled("Canvas 没有子实体");
            }
            else
            {
                foreach (int childId in rel.Children)
                {
                    string childName = _ecs.GetEntityName(childId) ?? $"Entity_{childId}";
                    bool hasUI = _ecs.HasComponent<UIElementComponent>(childId);
                    string icon = hasUI ? "🎨" : "◆";
                    ImGui.BulletText($"{icon} {childName} #{childId}");
                    if (hasUI && ImGui.IsItemClicked())
                    {
                        _selection.Select(childId);
                    }
                }
            }

            ImGui.Separator();

            // 添加新 UI 元素到 Canvas
            var elemTypes = new[] { "Text", "Image", "Button", "Slider", "CheckBox", "InputField", "ProgressBar", "Panel" };
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
            ImGui.Combo("##AddUIType", ref _addElementTypeIndex, elemTypes, elemTypes.Length);
            ImGui.SameLine();
            if (ImGui.Button("添加子元素"))
            {
                int newEntity = _ecs.CreateEntity($"UI_{elemTypes[_addElementTypeIndex]}");
                _ecs.AddComponent(newEntity, new UIElementComponent
                {
                    ElementType = (UIElementType)(_addElementTypeIndex + 1), // +1 因为 0 是 Canvas
                    Name = elemTypes[_addElementTypeIndex],
                });

                // 设置父子关系
                var canvasRel = _ecs.GetComponent<RelationshipComponent>(canvasEntityId);
                canvasRel.Children ??= new System.Collections.Generic.List<int>();
                canvasRel.Children.Add(newEntity);
                _ecs.SetComponent(canvasEntityId, canvasRel);

                var childRel = new RelationshipComponent { ParentId = canvasEntityId, Children = new System.Collections.Generic.List<int>() };
                _ecs.AddComponent(newEntity, childRel);

                OnSceneModified?.Invoke();
                _selection.Select(newEntity);
            }
        }
    }
}
