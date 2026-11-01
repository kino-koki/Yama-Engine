// ==================================================================================================
//  EcsInspectorPanel.cs — yama引擎 ECS 属性检查器面板
// ==================================================================================================
//  命名空间: YamaEngine.Editor.Panels
//  依赖:     ImGuiNET, YamaEngine.ECS, System.Reflection
//
//  功能:
//    显示并编辑当前选中 ECS 实体的组件属性:
//      * 实体头部: 名称编辑 + 实体 ID 显示
//      * Transform: Position/Rotation(欧拉角)/Scale 拖拽编辑
//      * 组件列表: 反射自动生成 UI
//          - float/int/bool/string/enum
//          - Vector2/Vector3/Vector4/Quaternion
//          - Guid (显示为字符串)
//      * 添加/删除组件
//      * 多选时显示公共属性
//      * 修改后触发 OnSceneModified
//
//  与 EcsSelectionState 联动，选中实体后自动显示其属性。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ImGuiNET;
using YamaEngine.ECS;
using YamaEngine.Particles;

namespace YamaEngine.Editor.Panels
{
    /// <summary>
    /// ECS 属性检查器面板。通过反射自动为 ECS 组件生成可视化编辑 UI。
    /// </summary>
    public sealed class EcsInspectorPanel
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;

        // ── UI 状态 ────────────────────────────────────────────────────────────────

        // 欧拉角缓存：避免拖动时万向锁抖动
        private readonly Dictionary<int, Vector3> _eulerCache = new();
        private bool _rotDragging;
        private int _addComponentIndex;

        // 可添加的组件类型
        private static readonly (string Label, Type Type)[] _addComponentTypes =
        {
            ("Mesh (网格)", typeof(MeshComponent)),
            ("Light (灯光)", typeof(LightComponent)),
            ("Camera (相机)", typeof(CameraComponent)),
            ("Tag (标签)", typeof(TagComponent)),
            ("Relationship (父子)", typeof(RelationshipComponent)),
            ("ParticleEmitter (粒子)", typeof(ParticleEmitterComponent)),
            ("UICanvas (画布)", typeof(UICanvasComponent)),
            ("UIElement (界面元素)", typeof(UIElementComponent)),
        };

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>场景被修改（需要标记 Dirty）。</summary>
        public event Action? OnSceneModified;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>窗口标题。</summary>
        public string Title { get; set; } = "ECS 检查器";

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public EcsInspectorPanel(EntityComponentSystem ecs, EcsSelectionState selection)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  渲染
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>渲染面板（由编辑器主循环每帧调用）。</summary>
        public void Render()
        {
            if (!IsVisible) return;

            ImGui.SetNextWindowSizeConstraints(new Vector2(260, 300), new Vector2(float.MaxValue, float.MaxValue));
            if (!ImGui.Begin($"{Title}###EcsInspector"))
            {
                ImGui.End();
                return;
            }

            int entityId = _selection.Active;

            if (entityId < 0 || !_ecs.HasEntity(entityId))
            {
                ImGui.TextDisabled("未选中任何实体");
                ImGui.TextDisabled("在层级面板中点击实体以编辑其属性。");
                ImGui.End();
                return;
            }

            // 多选提示
            if (_selection.Count > 1)
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1f),
                    $"多选 ({_selection.Count} 个实体) — 编辑活跃实体 #{entityId}");
                ImGui.Separator();
            }

            DrawEntityHeader(entityId);
            ImGui.Separator();

            // Transform（始终显示，因为 CreateEntity 默认添加）
            if (_ecs.HasComponent<TransformComponent>(entityId))
            {
                if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    DrawTransform(entityId);
                    ImGui.Unindent();
                }
            }

            // 其他组件
            DrawComponent<MeshComponent>(entityId, "Mesh", DrawMesh);
            DrawComponent<LightComponent>(entityId, "Light", DrawLight);
            DrawComponent<CameraComponent>(entityId, "Camera", DrawCamera);
            DrawComponent<TagComponent>(entityId, "Tag", DrawTag);
            DrawComponent<RelationshipComponent>(entityId, "Relationship", DrawRelationship);
            DrawComponent<ParticleEmitterComponent>(entityId, "Particle Emitter", DrawParticleEmitter);
            DrawComponent<UICanvasComponent>(entityId, "UI Canvas", DrawUICanvas);
            DrawComponent<UIElementComponent>(entityId, "UI Element", DrawUIElement);

            // 添加组件
            DrawAddComponent(entityId);

            ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  实体头部
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawEntityHeader(int entityId)
        {
            string name = _ecs.GetEntityName(entityId) ?? "Entity";

            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.InputText("##EntityName", ref name, 64))
            {
                _ecs.SetEntityName(entityId, name);
                OnSceneModified?.Invoke();
            }
            ImGui.PopItemWidth();

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
            ImGui.Text($"#{entityId}");
            ImGui.PopStyleColor();

            // 组件数量
            int compCount = CountComponents(entityId);
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"组件: {compCount}");
        }

        private int CountComponents(int entityId)
        {
            int count = 0;
            if (_ecs.HasComponent<TransformComponent>(entityId)) count++;
            if (_ecs.HasComponent<MeshComponent>(entityId)) count++;
            if (_ecs.HasComponent<LightComponent>(entityId)) count++;
            if (_ecs.HasComponent<CameraComponent>(entityId)) count++;
            if (_ecs.HasComponent<TagComponent>(entityId)) count++;
            if (_ecs.HasComponent<RelationshipComponent>(entityId)) count++;
            if (_ecs.HasComponent<ParticleEmitterComponent>(entityId)) count++;
            if (_ecs.HasComponent<UICanvasComponent>(entityId)) count++;
            if (_ecs.HasComponent<UIElementComponent>(entityId)) count++;
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Transform
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawTransform(int entityId)
        {
            var t = _ecs.GetComponent<TransformComponent>(entityId);

            // Position
            Vector3 pos = t.Position;
            if (DragFloat3("位置", "Pos", ref pos, 0.1f))
            {
                t.Position = pos;
                _ecs.SetComponent(entityId, t);
                OnSceneModified?.Invoke();
            }

            // Rotation (欧拉角)
            if (!_eulerCache.TryGetValue(entityId, out var euler) || !_rotDragging)
                euler = QuaternionToEulerDegrees(t.Rotation);
            _eulerCache[entityId] = euler;
            Vector3 e = euler;
            if (DragFloat3("旋转", "Rot", ref e, 0.5f, -360f, 360f, "%.1f"))
            {
                _rotDragging = true;
                t.Rotation = EulerDegreesToQuaternion(e);
                _eulerCache[entityId] = e;
                _ecs.SetComponent(entityId, t);
                OnSceneModified?.Invoke();
            }
            else _rotDragging = false;

            // Scale
            Vector3 scl = t.Scale;
            if (DragFloat3("缩放", "Scl", ref scl, 0.05f, 0.001f, 1000f))
            {
                t.Scale = scl;
                _ecs.SetComponent(entityId, t);
                OnSceneModified?.Invoke();
            }

            if (ImGui.Button("重置变换"))
            {
                t.Position = Vector3.Zero;
                t.Rotation = Quaternion.Identity;
                t.Scale = Vector3.One;
                _ecs.SetComponent(entityId, t);
                _eulerCache.Remove(entityId);
                OnSceneModified?.Invoke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Mesh 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawMesh(int entityId, ref MeshComponent mesh)
        {
            bool changed = false;

            // MeshId
            string meshIdStr = mesh.MeshId.ToString();
            ImGui.PushItemWidth(-1);
            if (ImGui.InputText("网格 GUID", ref meshIdStr, 64))
            {
                if (Guid.TryParse(meshIdStr, out var gid)) { mesh.MeshId = gid; changed = true; }
            }
            ImGui.PopItemWidth();

            // MaterialId
            string matIdStr = mesh.MaterialId.ToString();
            ImGui.PushItemWidth(-1);
            if (ImGui.InputText("材质 GUID", ref matIdStr, 64))
            {
                if (Guid.TryParse(matIdStr, out var gid)) { mesh.MaterialId = gid; changed = true; }
            }
            ImGui.PopItemWidth();

            changed |= ImGui.Checkbox("投射阴影", ref mesh.CastsShadow);
            changed |= ImGui.Checkbox("接收阴影", ref mesh.ReceivesShadow);
            changed |= ImGui.Checkbox("可见", ref mesh.IsVisible);

            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                $"顶点: {mesh.VertexCount}  三角形: {mesh.TriangleCount}");

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Light 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawLight(int entityId, ref LightComponent light)
        {
            bool changed = false;

            // 光源类型
            var types = new[] { "Directional", "Point", "Spot", "Area" };
            int typeIdx = (int)light.Type;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("类型", ref typeIdx, types, types.Length))
            {
                light.Type = (LightComponent.LightType)typeIdx;
                changed = true;
            }

            // 颜色
            Vector3 color = light.Color;
            if (ImGui.ColorEdit3("颜色", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
            {
                light.Color = color;
                changed = true;
            }

            changed |= DrawFloatSlider("强度", ref light.Intensity, 0f, 100f, "%.2f");
            changed |= DrawFloatSlider("范围", ref light.Range, 0.1f, 500f, "%.1f");

            if (light.Type == LightComponent.LightType.Spot)
            {
                changed |= DrawFloatSlider("内锥角", ref light.InnerConeAngle, 0f, 90f, "%.1f°");
                changed |= DrawFloatSlider("外锥角", ref light.OuterConeAngle, 0f, 90f, "%.1f°");
            }

            changed |= ImGui.Checkbox("投射阴影", ref light.CastsShadow);
            if (light.CastsShadow)
                changed |= DrawFloatSlider("阴影偏移", ref light.ShadowBias, 0f, 0.01f, "%.5f");

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Camera 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawCamera(int entityId, ref CameraComponent cam)
        {
            bool changed = false;

            changed |= DrawFloatSlider("FOV", ref cam.FieldOfView, 10f, 170f, "%.1f°");
            changed |= DrawFloatSlider("近裁面", ref cam.NearClip, 0.001f, 10f, "%.3f");
            changed |= DrawFloatSlider("远裁面", ref cam.FarClip, 10f, 10000f, "%.0f");
            changed |= DrawFloatSlider("宽高比", ref cam.AspectRatio, 0.1f, 10f, "%.3f");

            changed |= ImGui.Checkbox("正交", ref cam.IsOrthographic);
            if (cam.IsOrthographic)
                changed |= DrawFloatSlider("正交尺寸", ref cam.OrthographicSize, 0.1f, 100f, "%.1f");

            changed |= ImGui.Checkbox("主相机", ref cam.IsMain);

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Tag 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawTag(int entityId, ref TagComponent tag)
        {
            bool changed = false;

            string tagStr = tag.Tag ?? "";
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("标签", ref tagStr, 64))
            {
                tag.Tag = tagStr;
                changed = true;
            }
            ImGui.PopItemWidth();

            changed |= ImGui.DragInt("层", ref tag.Layer, 0.1f, 0, 31);
            changed |= ImGui.Checkbox("静态", ref tag.IsStatic);

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  ParticleEmitter 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawParticleEmitter(int entityId, ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            bool enabled = emitter.Enabled;
            if (ImGui.Checkbox("启用", ref enabled))
            {
                emitter.Enabled = enabled;
                changed = true;
            }

            int maxParticles = emitter.MaxParticles;
            if (ImGui.DragInt("最大粒子数", ref maxParticles, 1, 1, 100000))
            {
                emitter.MaxParticles = maxParticles;
                changed = true;
            }

            float rate = emitter.EmissionRate;
            if (ImGui.DragFloat("发射速率", ref rate, 0.5f, 0f, 10000f, "%.1f"))
            {
                emitter.EmissionRate = rate;
                changed = true;
            }

            float lifetime = emitter.Lifetime;
            if (ImGui.DragFloat("生命周期", ref lifetime, 0.1f, 0.01f, 60f, "%.2f"))
            {
                emitter.Lifetime = lifetime;
                changed = true;
            }

            float size = emitter.StartSize;
            if (ImGui.DragFloat("起始大小", ref size, 0.005f, 0.001f, 10f, "%.3f"))
            {
                emitter.StartSize = size;
                changed = true;
            }

            Vector4 color = emitter.StartColor;
            if (ImGui.ColorEdit4("起始颜色", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
            {
                emitter.StartColor = color;
                changed = true;
            }

            var shapes = new[] { "Sphere", "Hemisphere", "Cone", "Box" };
            int shapeIdx = (int)emitter.ShapeType;
            if (ImGui.Combo("形状", ref shapeIdx, shapes, shapes.Length))
            {
                emitter.ShapeType = (ParticleShapeType)shapeIdx;
                changed = true;
            }

            float radius = emitter.ShapeRadius;
            if (ImGui.DragFloat("形状半径", ref radius, 0.05f, 0f, 100f, "%.2f"))
            {
                emitter.ShapeRadius = radius;
                changed = true;
            }

            float gravity = emitter.Gravity;
            if (ImGui.DragFloat("重力", ref gravity, 0.5f, -50f, 50f, "%.2f"))
            {
                emitter.Gravity = gravity;
                changed = true;
            }

            float speed = emitter.StartSpeed;
            if (ImGui.DragFloat("起始速度", ref speed, 0.1f, -100f, 100f, "%.2f"))
            {
                emitter.StartSpeed = speed;
                changed = true;
            }

            bool looping = emitter.Looping;
            if (ImGui.Checkbox("循环", ref looping))
            {
                emitter.Looping = looping;
                changed = true;
            }

            var spaces = new[] { "World", "Local" };
            int spaceIdx = (int)emitter.SimulationSpace;
            if (ImGui.Combo("模拟空间", ref spaceIdx, spaces, spaces.Length))
            {
                emitter.SimulationSpace = (ParticleSimulationSpace)spaceIdx;
                changed = true;
            }

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  UI Canvas 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawUICanvas(int entityId, ref UICanvasComponent canvas)
        {
            bool changed = false;

            bool enabled = canvas.Enabled;
            if (ImGui.Checkbox("启用", ref enabled))
            {
                canvas.Enabled = enabled;
                changed = true;
            }

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

            bool receiveInput = canvas.ReceiveInput;
            if (ImGui.Checkbox("接收输入", ref receiveInput))
            {
                canvas.ReceiveInput = receiveInput;
                changed = true;
            }

            int sortOrder = canvas.SortOrder;
            if (ImGui.DragInt("排序顺序", ref sortOrder, 1, -100, 100))
            {
                canvas.SortOrder = sortOrder;
                changed = true;
            }

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  UI Element 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawUIElement(int entityId, ref UIElementComponent element)
        {
            bool changed = false;

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

            bool visible = element.IsVisible;
            if (ImGui.Checkbox("可见", ref visible))
            {
                element.IsVisible = visible;
                changed = true;
            }
            bool interactive = element.IsEnabled;
            if (ImGui.Checkbox("启用交互", ref interactive))
            {
                element.IsEnabled = interactive;
                changed = true;
            }

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
            if (ImGui.DragFloat4("偏移", ref offsets, 0.5f, -1000f, 1000f))
            {
                element.Offsets = offsets;
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

            // 类型专用属性（简化版）
            if (element.ElementType == UIElementType.Text || element.ElementType == UIElementType.Button || element.ElementType == UIElementType.CheckBox)
            {
                string text = element.Text ?? "";
                if (ImGui.InputText("文本", ref text, 128))
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
            }

            if (element.ElementType == UIElementType.Image || element.ElementType == UIElementType.Button || element.ElementType == UIElementType.Panel)
            {
                Vector4 bgColor = element.BackgroundColor;
                if (ImGui.ColorEdit4("背景颜色", ref bgColor, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
                {
                    element.BackgroundColor = bgColor;
                    changed = true;
                }
            }

            if (element.ElementType == UIElementType.Slider || element.ElementType == UIElementType.ProgressBar)
            {
                float curVal = element.CurrentValue;
                if (ImGui.SliderFloat("当前值", ref curVal, element.MinValue, element.MaxValue))
                {
                    element.CurrentValue = curVal;
                    changed = true;
                }
            }

            if (changed) OnSceneModified?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Relationship 组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawRelationship(int entityId, ref RelationshipComponent rel)
        {
            ImGui.Text($"父实体: {(rel.ParentId == 0 ? "(根)" : $"#{rel.ParentId}")}");

            if (rel.Children == null || rel.Children.Count == 0)
            {
                ImGui.TextDisabled("无子实体");
                return;
            }

            ImGui.Text($"子实体 ({rel.Children.Count}):");
            for (int i = 0; i < rel.Children.Count; i++)
            {
                int childId = rel.Children[i];
                string childName = _ecs.HasEntity(childId) ? _ecs.GetEntityName(childId) : "???";
                ImGui.BulletText($"#{childId}: {childName}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  泛型组件绘制框架
        // ─────────────────────────────────────────────────────────────────────────

        private delegate void ComponentDrawer<T>(int entityId, ref T component) where T : struct, IComponent;

        private void DrawComponent<T>(int entityId, string headerLabel, ComponentDrawer<T> drawer)
            where T : struct, IComponent
        {
            if (!_ecs.HasComponent<T>(entityId)) return;

            ImGui.PushID(typeof(T).Name);

            if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                var comp = _ecs.GetComponent<T>(entityId);
                drawer(entityId, ref comp);
                _ecs.SetComponent(entityId, comp);

                ImGui.Separator();
                if (ImGui.Button("删除组件"))
                {
                    _ecs.RemoveComponent<T>(entityId);
                    OnSceneModified?.Invoke();
                }

                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  添加组件
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawAddComponent(int entityId)
        {
            ImGui.Separator();

            string[] labels = _addComponentTypes.Select(t => t.Label).ToArray();

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
            ImGui.Combo("##AddCompType", ref _addComponentIndex, labels, labels.Length);

            ImGui.SameLine();
            if (ImGui.Button("添加"))
            {
                var (_, type) = _addComponentTypes[_addComponentIndex];
                AddComponentByType(entityId, type);
                OnSceneModified?.Invoke();
            }
        }

        private void AddComponentByType(int entityId, Type componentType)
        {
            if (componentType == typeof(MeshComponent))
            {
                if (!_ecs.HasComponent<MeshComponent>(entityId))
                    _ecs.AddComponent(entityId, new MeshComponent { IsVisible = true, CastsShadow = true });
            }
            else if (componentType == typeof(LightComponent))
            {
                if (!_ecs.HasComponent<LightComponent>(entityId))
                    _ecs.AddComponent(entityId, new LightComponent
                    {
                        Type = LightComponent.LightType.Point,
                        Color = Vector3.One,
                        Intensity = 1f,
                        Range = 10f,
                    });
            }
            else if (componentType == typeof(CameraComponent))
            {
                if (!_ecs.HasComponent<CameraComponent>(entityId))
                    _ecs.AddComponent(entityId, new CameraComponent
                    {
                        FieldOfView = 60f,
                        NearClip = 0.1f,
                        FarClip = 1000f,
                        AspectRatio = 16f / 9f,
                    });
            }
            else if (componentType == typeof(TagComponent))
            {
                if (!_ecs.HasComponent<TagComponent>(entityId))
                    _ecs.AddComponent(entityId, new TagComponent { Tag = "Untagged", Layer = 0 });
            }
            else if (componentType == typeof(RelationshipComponent))
            {
                if (!_ecs.HasComponent<RelationshipComponent>(entityId))
                    _ecs.AddComponent(entityId, new RelationshipComponent { Children = new List<int>() });
            }
            else if (componentType == typeof(ParticleEmitterComponent))
            {
                if (!_ecs.HasComponent<ParticleEmitterComponent>(entityId))
                    _ecs.AddComponent(entityId, new ParticleEmitterComponent());
            }
            else if (componentType == typeof(UICanvasComponent))
            {
                if (!_ecs.HasComponent<UICanvasComponent>(entityId))
                    _ecs.AddComponent(entityId, new UICanvasComponent());
            }
            else if (componentType == typeof(UIElementComponent))
            {
                if (!_ecs.HasComponent<UIElementComponent>(entityId))
                    _ecs.AddComponent(entityId, new UIElementComponent());
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  ImGui 辅助方法
        // ─────────────────────────────────────────────────────────────────────────

        private bool DragFloat3(string label, string id, ref Vector3 value, float speed,
            float min = float.MinValue, float max = float.MaxValue, string format = "%.2f")
        {
            ImGui.PushID(id);

            // 标签列
            ImGui.AlignTextToFramePadding();
            ImGui.Text(label);
            ImGui.SameLine();

            float avail = ImGui.GetContentRegionAvail().X;
            float colWidth = (avail - 10) / 3f;

            bool changed = false;
            string[] labels = { "X", "Y", "Z" };

            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, 5);

                ImGui.PushItemWidth(colWidth);
                ImGui.PushStyleColor(ImGuiCol.Text, i == 0
                    ? new Vector4(1f, 0.4f, 0.4f, 1f)
                    : i == 1 ? new Vector4(0.4f, 1f, 0.4f, 1f)
                             : new Vector4(0.4f, 0.6f, 1f, 1f));

                float v = value[i];
                if (ImGui.DragFloat($"##{labels[i]}", ref v, speed, min, max, format))
                {
                    value[i] = v;
                    changed = true;
                }

                ImGui.PopStyleColor();
                ImGui.PopItemWidth();
            }

            ImGui.PopID();
            return changed;
        }

        private bool DrawFloatSlider(string label, ref float value, float min, float max, string format = "%.2f")
        {
            ImGui.PushID(label);
            ImGui.AlignTextToFramePadding();
            ImGui.Text(label);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            bool changed = ImGui.SliderFloat("##v", ref value, min, max, format);
            ImGui.PopID();
            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  四元数 ↔ 欧拉角转换
        // ─────────────────────────────────────────────────────────────────────────

        private static Vector3 QuaternionToEulerDegrees(Quaternion q)
        {
            // ZYX 顺序 (Yaw=Z, Pitch=Y, Roll=X)
            float sqw = q.W * q.W;
            float sqx = q.X * q.X;
            float sqy = q.Y * q.Y;
            float sqz = q.Z * q.Z;

            float unit = sqx + sqy + sqz + sqw;
            float test = q.X * q.Y + q.Z * q.W;

            float yaw, pitch, roll;

            if (test > 0.499f * unit)
            {
                // 北极 singularity
                yaw = 2f * MathF.Atan2(q.X, q.W);
                pitch = MathF.PI / 2f;
                roll = 0f;
            }
            else if (test < -0.499f * unit)
            {
                // 南极 singularity
                yaw = -2f * MathF.Atan2(q.X, q.W);
                pitch = -MathF.PI / 2f;
                roll = 0f;
            }
            else
            {
                yaw = MathF.Atan2(2f * (q.Y * q.W - q.X * q.Z), sqx - sqy - sqz + sqw);
                pitch = MathF.Asin(2f * test / unit);
                roll = MathF.Atan2(2f * (q.X * q.W + q.Y * q.Z), -sqx + sqy - sqz + sqw);
            }

            return new Vector3(
                roll * (180f / MathF.PI),
                pitch * (180f / MathF.PI),
                yaw * (180f / MathF.PI));
        }

        private static Quaternion EulerDegreesToQuaternion(Vector3 euler)
        {
            float rollRad = euler.X * (MathF.PI / 180f);
            float pitchRad = euler.Y * (MathF.PI / 180f);
            float yawRad = euler.Z * (MathF.PI / 180f);

            float cr = MathF.Cos(rollRad * 0.5f);
            float sr = MathF.Sin(rollRad * 0.5f);
            float cp = MathF.Cos(pitchRad * 0.5f);
            float sp = MathF.Sin(pitchRad * 0.5f);
            float cy = MathF.Cos(yawRad * 0.5f);
            float sy = MathF.Sin(yawRad * 0.5f);

            return new Quaternion(
                sr * cp * cy - cr * sp * sy,
                cr * sp * cy + sr * cp * sy,
                cr * cp * sy - sr * sp * cy,
                cr * cp * cy + sr * sp * sy);
        }
    }
}
