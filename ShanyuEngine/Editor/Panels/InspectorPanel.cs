using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor;
using ShanyuEngine.Input;

namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 属性检查器面板 (InspectorPanel)
    //
    // 显示并编辑当前选中对象的属性:
    //   * 对象头部 (名称 / 可见 / 锁定 / 层 / Prefab 标记)
    //   * Transform: Position / Rotation / Scale (可拖拽数值输入, 旋转以欧拉角显示)
    //   * 组件列表: 反射自动生成 UI (float/int/bool/string/Vector3/Color/Enum/资产引用)
    //   * 材质: 颜色选择器 / 纹理槽 / 参数滑块
    //   * 组件添加 / 删除按钮
    //   * Prefab 覆盖标记 (被覆盖的属性以 * 与高亮显示)
    // ════════════════════════════════════════════════════════════════════════

    public sealed class InspectorPanel : EditorPanel
    {
        private SceneObject _target;
        // 欧拉角缓存 (按对象 Id), 避免拖动时万向锁抖动
        private readonly Dictionary<Guid, Vector3> _eulerCache = new();
        // Prefab 覆盖字段标记 (按对象 Id -> 字段名集合)
        private readonly Dictionary<Guid, HashSet<string>> _overrides = new();

        // 可添加的组件类型
        private static readonly (string Label, Type Type)[] _addComponentTypes =
        {
            ("相机 (Camera)", typeof(CameraComponent)),
            ("灯光 (Light)", typeof(LightComponent)),
        };
        private int _addComponentIndex;

        public InspectorPanel() : base("检查器", "Inspector")
        {
            CanClose = false;
            MinSize = new Vector2(240, 260);
        }

        /// <summary>显式设置检查目标 (供选择系统同步调用)。</summary>
        public void SetTarget(SceneObject obj) => _target = obj;

        protected override void OnDraw(EditorContext ctx)
        {
            _target = ctx.Selection.Active ?? _target;
            if (_target == null || !ctx.Assets.SceneObjects.Contains(_target))
            {
                ImGui.TextDisabled("未选中任何对象");
                ImGui.TextDisabled("在大纲或视口中点击对象以编辑其属性。");
                return;
            }

            DrawHeader(ctx, _target);
            ImGui.Separator();

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                DrawTransform(ctx, _target);
                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader("材质", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                DrawMaterial(ctx, _target);
                ImGui.Unindent();
            }

            // 各组件
            foreach (var comp in _target.Components.ToArray())
                DrawComponent(ctx, _target, comp);

            DrawAddComponent(ctx, _target);
        }

        // ─────────────────────── 头部 ───────────────────────

        private void DrawHeader(EditorContext ctx, SceneObject obj)
        {
            // 名称
            string name = obj.Name;
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.InputText("##ObjName", ref name, 64)) { obj.Name = name; MarkDirty(ctx); }
            ImGui.PopItemWidth();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, obj.IsVisible ? new Vector4(0.7f, 0.7f, 0.7f, 1f) : new Vector4(0.9f, 0.3f, 0.3f, 1f));
            if (ImGui.Button(obj.IsVisible ? "V" : "H", new Vector2(24, 0))) obj.IsVisible = !obj.IsVisible;
            ImGui.PopStyleColor();

            // 活动状态 / 层
            bool active = obj.IsVisible;
            ImGui.Checkbox("活动", ref active);
            obj.IsVisible = active;

            ImGui.SameLine();
            ImGui.Text("层");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            var layers = ctx.World.Layers;
            string[] layerNames = layers.Layers.Select(l => $"{l.Index}: {l.Name}").ToArray();
            int layerIdx = Math.Clamp(obj.Layer, 0, Math.Max(0, layerNames.Length - 1));
            if (ImGui.Combo("##Layer", ref layerIdx, layerNames, layerNames.Length))
            {
                obj.Layer = layerIdx;
                MarkDirty(ctx);
            }

            // Prefab 标记
            if (!string.IsNullOrEmpty(obj.PrefabSourceGuid))
            {
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 1.0f, 1f));
                ImGui.Text($"Prefab 实例  ({obj.PrefabSourceGuid})");
                ImGui.PopStyleColor();
                ImGui.SameLine();
                if (ImGui.Button("应用覆盖")) ctx.World.PrefabEdit.ApplyOverrides(obj.PrefabSourceGuid + ".prefab");
                ImGui.SameLine();
                if (ImGui.Button("还原")) ctx.World.PrefabEdit.RevertOverrides();
            }
        }

        // ─────────────────────── Transform ───────────────────────

        private void DrawTransform(EditorContext ctx, SceneObject obj)
        {
            // Position
            Vector3 pos = obj.Position;
            if (DragFloat3Row("位置", "Pos", ref pos, 0.1f, float.MinValue, float.MaxValue))
            {
                obj.Position = pos;
                if (obj.Parent == null) obj.LocalPosition = pos;
                MarkDirty(ctx);
            }

            // Rotation (欧拉角显示)
            if (!_eulerCache.TryGetValue(obj.Id, out var euler) || !_rotDragging)
                euler = obj.Rotation.ToEulerDegrees();
            _eulerCache[obj.Id] = euler;
            Vector3 e = euler;
            if (DragFloat3Row("旋转", "Rot", ref e, 0.5f, -360f, 360f, "%.1f"))
            {
                _rotDragging = true;
                obj.Rotation = EditorMathExtensions.FromEulerDegrees(e);
                _eulerCache[obj.Id] = e;
                MarkDirty(ctx);
            }
            else _rotDragging = false;

            // Scale
            Vector3 scl = obj.Scale;
            if (DragFloat3Row("缩放", "Scl", ref scl, 0.05f, 0.001f, 1000f))
            {
                obj.Scale = scl;
                MarkDirty(ctx);
            }

            // 重置按钮
            if (ImGui.Button("重置变换"))
            {
                obj.Position = Vector3.Zero;
                obj.Rotation = Quaternion.Identity;
                obj.Scale = Vector3.One;
                _eulerCache.Remove(obj.Id);
                MarkDirty(ctx);
            }
        }

        private bool _rotDragging;

        // ─────────────────────── 材质 ───────────────────────

        private void DrawMaterial(EditorContext ctx, SceneObject obj)
        {
            // 颜色选择器
            Vector3 col = obj.BaseColor;
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("基础色").X - 8);
            if (ImGui.ColorEdit3("基础色", ref col, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
            {
                obj.BaseColor = col;
                MarkDirty(ctx);
            }
            ImGui.PopItemWidth();

            // 参数滑块
            float metallic = obj.Metallic, roughness = obj.Roughness;
            if (ImGui.SliderFloat("金属度", ref metallic, 0f, 1f)) { obj.Metallic = metallic; MarkDirty(ctx); }
            if (ImGui.SliderFloat("粗糙度", ref roughness, 0.02f, 1f)) { obj.Roughness = roughness; MarkDirty(ctx); }

            ImGui.Checkbox("投射阴影", ref obj.CastShadow);
            ImGui.SameLine();
            ImGui.Checkbox("接收阴影", ref obj.ReceiveShadow);

            // 纹理槽
            ImGui.TextDisabled("纹理槽");
            string[] slots = { "Albedo", "Normal", "Roughness", "Metallic", "Emission" };
            float slotSize = 56f;
            float spacing = 8f;
            for (int i = 0; i < slots.Length; i++)
            {
                if (i > 0) ImGui.SameLine(0, spacing);
                ImGui.PushID(i);
                Vector4 slotCol = i == 0 ? new Vector4(col, 1f) : new Vector4(0.3f, 0.3f, 0.3f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Button, slotCol);
                ImGui.Button("##slot", new Vector2(slotSize, slotSize));
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(slots[i]);
                // 接受内容浏览器拖放的资产路径
                if (ImGui.BeginDragDropTarget())
                {
                    var p = ImGui.AcceptDragDropPayload("CONTENT_ASSET");
                    if (p.NativePtr != null && ctx.Editor?.ContentBrowser?.DraggingAssetPath is string path)
                    {
                        obj.MaterialAssetPath = path;
                        MarkDirty(ctx);
                        ctx.Console?.AddLog(LogLevel.Info, $"已将 '{path}' 指定到纹理槽 {slots[i]}", "Inspector");
                    }
                    ImGui.EndDragDropTarget();
                }
                ImGui.PopID();
            }

            // 资产引用
            string meshPath = obj.MeshAssetPath ?? string.Empty;
            string matPath = obj.MaterialAssetPath ?? string.Empty;
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 110);
            if (ImGui.InputText("网格路径", ref meshPath, 128)) { obj.MeshAssetPath = meshPath; MarkDirty(ctx); }
            if (ImGui.InputText("材质路径", ref matPath, 128)) { obj.MaterialAssetPath = matPath; MarkDirty(ctx); }
            ImGui.PopItemWidth();
        }

        // ─────────────────────── 组件 (反射) ───────────────────────

        private void DrawComponent(EditorContext ctx, SceneObject obj, IComponent comp)
        {
            ImGui.PushID(comp.GetHashCode());
            bool open = ImGui.CollapsingHeader(comp.Name + "##comp", ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine(ImGui.GetWindowWidth() - 34);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
            if (ImGui.SmallButton("x##RemoveComp"))
            {
                var t = comp.GetType();
                obj.GetType().GetMethod("RemoveComponent")?.MakeGenericMethod(t).Invoke(obj, null);
                MarkDirty(ctx);
            }
            ImGui.PopStyleColor();
            if (!open) { ImGui.PopID(); return; }

            ImGui.Indent();
            var type = comp.GetType();
            foreach (var fi in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsInitOnly) continue;
                ImGui.PushID(fi.Name);
                DrawReflectedField(ctx, obj, comp, fi);
                ImGui.PopID();
            }
            ImGui.Unindent();
            ImGui.PopID();
        }

        private void DrawReflectedField(EditorContext ctx, SceneObject obj, object comp, FieldInfo fi)
        {
            bool isOverride = IsOverride(obj, fi.Name);
            string label = (isOverride ? "* " : "") + fi.Name;
            var ft = fi.FieldType;

            if (ft == typeof(float))
            {
                float v = (float)fi.GetValue(comp);
                float min = 0f, max = 100f, speed = 0.1f;
                string lname = fi.Name.ToLowerInvariant();
                if (lname.Contains("angle")) { min = 0f; max = 360f; speed = 0.5f; }
                else if (lname.Contains("intensity")) { min = 0f; max = 10000f; speed = 1f; }
                else if (lname.Contains("near") || lname.Contains("bias") || lname.Contains("rough") || lname.Contains("metallic")) { min = 0f; max = 1f; speed = 0.01f; }
                if (ImGui.DragFloat(label, ref v, speed, min, max, "%.3f"))
                {
                    fi.SetValue(comp, v);
                    MarkOverride(ctx, obj, fi.Name);
                    MarkDirty(ctx);
                }
            }
            else if (ft == typeof(int))
            {
                int v = (int)fi.GetValue(comp);
                if (ImGui.DragInt(label, ref v, 0.5f, int.MinValue, int.MaxValue)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
            }
            else if (ft == typeof(bool))
            {
                bool v = (bool)fi.GetValue(comp);
                if (ImGui.Checkbox(label, ref v)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
            }
            else if (ft == typeof(string))
            {
                string v = (string)fi.GetValue(comp) ?? string.Empty;
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X - 12);
                if (ImGui.InputText(label, ref v, 128)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
                ImGui.PopItemWidth();
            }
            else if (ft == typeof(Vector3))
            {
                Vector3 v = (Vector3)fi.GetValue(comp);
                if (DragFloat3Row(label, fi.Name, ref v, 0.05f, float.MinValue, float.MaxValue)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
            }
            else if (ft == typeof(Vector4))
            {
                Vector4 v = (Vector4)fi.GetValue(comp);
                if (fi.Name.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (ImGui.ColorEdit4(label, ref v)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
                }
                else
                {
                    if (ImGui.DragFloat4(label, ref v, 0.05f, float.MinValue, float.MaxValue)) { fi.SetValue(comp, v); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
                }
            }
            else if (ft == typeof(Quaternion))
            {
                var q = (Quaternion)fi.GetValue(comp);
                Vector3 e = q.ToEulerDegrees();
                if (DragFloat3Row(label + " (欧拉)", fi.Name, ref e, 0.5f, -360f, 360f)) { fi.SetValue(comp, EditorMathExtensions.FromEulerDegrees(e)); MarkOverride(ctx, obj, fi.Name); MarkDirty(ctx); }
            }
            else if (ft.IsEnum)
            {
                var cur = fi.GetValue(comp);
                var names = Enum.GetNames(ft);
                var values = Enum.GetValues(ft);
                int idx = Array.IndexOf(values, cur);
                if (idx < 0) idx = 0;
                if (ImGui.Combo(label, ref idx, names, names.Length))
                {
                    fi.SetValue(comp, values.GetValue(idx));
                    MarkOverride(ctx, obj, fi.Name);
                    MarkDirty(ctx);
                }
            }
            else
            {
                // 资产引用 / 其他: 显示类型名与字符串化值
                var v = fi.GetValue(comp);
                ImGui.TextDisabled($"{label}: {v?.ToString() ?? "(null)"}  [{ft.Name}]");
            }
        }

        // ─────────────────────── 添加组件 ───────────────────────

        private void DrawAddComponent(EditorContext ctx, SceneObject obj)
        {
            ImGui.Separator();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90);
            string[] labels = _addComponentTypes.Select(t => t.Label).ToArray();
            ImGui.Combo("##AddCompType", ref _addComponentIndex, labels, labels.Length);
            ImGui.SameLine();
            if (ImGui.Button("添加组件"))
            {
                var type = _addComponentTypes[_addComponentIndex].Type;
                if (!HasComponent(obj, type))
                {
                    var add = obj.GetType().GetMethod("AddComponent").MakeGenericMethod(type);
                    add.Invoke(obj, null);
                    MarkDirty(ctx);
                    ctx.Console?.AddLog(LogLevel.Info, $"已添加组件: {type.Name}", "Inspector");
                }
                else
                {
                    ctx.Console?.AddLog(LogLevel.Warning, $"对象已包含组件: {type.Name}", "Inspector");
                }
            }
        }

        // ─────────────────────── 工具 ───────────────────────

        private static bool DragFloat3Row(string label, string id, ref Vector3 v, float speed, float min, float max, string fmt = "%.3f")
        {
            ImGui.TextDisabled(label);
            ImGui.SameLine();
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
            bool changed = ImGui.DragFloat3("##" + id, ref v, speed, min, max, fmt);
            ImGui.PopItemWidth();
            return changed;
        }

        // SceneObject 只有泛型 HasComponent<T>, 这里通过反射按 Type 调用。
        private static bool HasComponent(SceneObject obj, Type type)
        {
            var mi = typeof(SceneObject).GetMethod("HasComponent")?.MakeGenericMethod(type);
            return mi != null && (bool)mi.Invoke(obj, null);
        }


        private bool IsOverride(SceneObject obj, string field)
        {
            return _overrides.TryGetValue(obj.Id, out var set) && set.Contains(field);
        }

        private void MarkOverride(EditorContext ctx, SceneObject obj, string field)
        {
            if (!string.IsNullOrEmpty(obj.PrefabSourceGuid))
            {
                if (!_overrides.TryGetValue(obj.Id, out var set)) { set = new HashSet<string>(); _overrides[obj.Id] = set; }
                set.Add(field);
                ctx.World.PrefabEdit.MarkOverride(field);
            }
        }

        private static void MarkDirty(EditorContext ctx) => ctx.World?.SceneManager.MarkDirty();
    }
}
