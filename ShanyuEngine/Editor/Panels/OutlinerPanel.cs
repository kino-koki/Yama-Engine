using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor;
using ShanyuEngine.Input;

namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 大纲面板 (OutlinerPanel)
    //
    // 以树形结构显示场景中所有实体, 支持:
    //   * 父子层级展开 / 折叠
    //   * 单击选中 / Ctrl 多选 / Shift 范围选
    //   * 右键菜单 (创建 / 删除 / 复制 / 重命名 / 设为根)
    //   * 拖拽重排父子关系
    //   * 搜索过滤
    //   * 可见性切换 (眼睛) / 锁定切换 (锁)
    //   * 实体类型图标 (网格 / 灯光 / 相机 / 空)
    // ════════════════════════════════════════════════════════════════════════

    public sealed class OutlinerPanel : EditorPanel
    {
        private string _search = string.Empty;
        private bool _showHidden = true;
        private SceneObject _dragObject;
        private SceneObject _renameTarget;
        private string _renameText = string.Empty;
        private int _anchorIndex = -1;

        // 当前帧可见对象的扁平列表 (用于 Shift 范围选)
        private readonly List<SceneObject> _flat = new();
        // 锁定集合 (按对象 Id)
        private readonly HashSet<Guid> _locked = new();

        public OutlinerPanel() : base("大纲", "Outliner")
        {
            CanClose = false;
            MinSize = new Vector2(180, 200);
        }

        protected override void OnDraw(EditorContext ctx)
        {
            DrawToolbar(ctx);

            ImGui.Separator();
            ImGui.BeginChild("##OutlinerTree", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);

            _flat.Clear();
            var assets = ctx.Assets;

            if (!string.IsNullOrWhiteSpace(_search))
            {
                // 搜索模式: 扁平显示匹配项
                foreach (var obj in assets.SceneObjects)
                {
                    if (obj == null) continue;
                    if (!obj.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
                    _flat.Add(obj);
                    DrawObjectRow(ctx, obj, 0);
                }
            }
            else
            {
                // 层级模式: 仅渲染根节点 (Parent == null)
                foreach (var obj in assets.SceneObjects)
                {
                    if (obj == null || obj.Parent != null) continue;
                    DrawObjectRecursive(ctx, obj, 0);
                }
            }

            // 空白处右键菜单 (创建根对象)
            if (ImGui.BeginPopupContextWindow("##OutlinerBg", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
            {
                DrawCreateMenu(ctx, null);
                ImGui.EndPopup();
            }

            // 空白处作为拖放目标 -> 设为根
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("OUTLINER_NODE");
                if (payload.NativePtr != null && _dragObject != null)
                {
                    _dragObject.DetachFromParent();
                    ctx.Console?.AddLog(LogLevel.Info, $"已将 '{_dragObject.Name}' 设为根对象", "Outliner");
                    _dragObject = null;
                }
                ImGui.EndDragDropTarget();
            }

            ImGui.EndChild();

            _expandAll = false;
            _collapseAll = false;
        }

        private void DrawToolbar(EditorContext ctx)
        {
            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 110);
            ImGui.InputTextWithHint("##OutlinerSearch", "搜索对象...", ref _search, 128);
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.Button("展开全部")) _expandAll = true;
            ImGui.SameLine();
            if (ImGui.Button("折叠")) _collapseAll = true;
        }

        private bool _expandAll;
        private bool _collapseAll;

        private void DrawObjectRecursive(EditorContext ctx, SceneObject obj, int depth)
        {
            _flat.Add(obj);
            ImGui.PushID(obj.Id.GetHashCode());

            var (icon, iconColor) = GetTypeIcon(obj);
            bool selected = obj.IsSelected;
            bool locked = _locked.Contains(obj.Id);

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                        ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
            if (selected) flags |= ImGuiTreeNodeFlags.Selected;
            if (obj.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

            if (_expandAll) ImGui.SetNextItemOpen(true);
            if (_collapseAll) ImGui.SetNextItemOpen(false);

            // 重命名中: 显示输入框
            bool renamed = false;
            if (_renameTarget == obj)
            {
                ImGui.SetNextItemOpen(true);
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 60);
                if (ImGui.InputText("##Rename", ref _renameText, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (!string.IsNullOrWhiteSpace(_renameText)) obj.Name = _renameText;
                    _renameTarget = null;
                }
                ImGui.PopItemWidth();
                if (ImGui.IsItemHovered() == false && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _renameTarget = null;
                renamed = true;
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, iconColor);
                ImGui.TextUnformatted(icon);
                ImGui.PopStyleColor();
                ImGui.SameLine(0, 4);
                bool open = ImGui.TreeNodeEx(obj.Name + "##" + obj.Id, flags);
                HandleInteraction(ctx, obj, locked);
                DrawDragDrop(ctx, obj);
                if (open && obj.Children.Count > 0)
                {
                    foreach (var child in obj.Children.ToArray())
                        DrawObjectRecursive(ctx, child, depth + 1);
                    ImGui.TreePop();
                }
            }

            // 可见性 / 锁定 切换 (右侧)
            if (!renamed)
            {
                ImGui.SameLine(ImGui.GetWindowWidth() - 50);
                ImGui.PushStyleColor(ImGuiCol.Text, obj.IsVisible ? new Vector4(0.7f) : new Vector4(0.9f, 0.3f, 0.3f, 1f));
                if (ImGui.SmallButton(obj.IsVisible ? "V" : "H")) obj.IsVisible = !obj.IsVisible;
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, locked ? new Vector4(1f, 0.7f, 0.2f, 1f) : new Vector4(0.5f));
                if (ImGui.SmallButton(locked ? "L" : "U"))
                {
                    if (locked) _locked.Remove(obj.Id); else _locked.Add(obj.Id);
                }
                ImGui.PopStyleColor();
            }

            ImGui.PopID();
        }

        private void DrawObjectRow(EditorContext ctx, SceneObject obj, int depth)
        {
            // 搜索模式下的单行显示
            ImGui.PushID(obj.Id.GetHashCode() + 1);
            var (icon, iconColor) = GetTypeIcon(obj);
            ImGui.PushStyleColor(ImGuiCol.Text, iconColor);
            ImGui.TextUnformatted(icon);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 4);
            bool selected = obj.IsSelected;
            if (ImGui.Selectable(obj.Name + "##" + obj.Id, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
            {
                HandleClick(ctx, obj, false);
            }
            DrawDragDrop(ctx, obj);
            DrawContextMenu(ctx, obj);
            ImGui.PopID();
        }

        private void HandleInteraction(EditorContext ctx, SceneObject obj, bool locked)
        {
            if (locked) return;
            if (ImGui.IsItemToggledOpen() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                // 双击进入重命名
                _renameTarget = obj;
                _renameText = obj.Name;
                return;
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                HandleClick(ctx, obj, true);
            }
            DrawContextMenu(ctx, obj);
        }

        private void HandleClick(EditorContext ctx, SceneObject obj, bool supportRange)
        {
            var sel = ctx.Selection;
            bool ctrl = ctx.Input.IsKeyDown(InputKey.Control);
            bool shift = ctx.Input.IsKeyDown(InputKey.Shift);

            if (supportRange && shift && _anchorIndex >= 0)
            {
                int idx = _flat.IndexOf(obj);
                if (idx >= 0)
                {
                    int a = Math.Min(_anchorIndex, idx);
                    int b = Math.Max(_anchorIndex, idx);
                    var range = _flat.Skip(a).Take(b - a + 1).Where(o => !_locked.Contains(o.Id)).ToList();
                    sel.Select(range, additive: false);
                }
            }
            else if (ctrl)
            {
                if (sel.Selected.Contains(obj)) sel.Deselect(obj);
                else sel.Select(obj, additive: true);
                _anchorIndex = _flat.IndexOf(obj);
            }
            else
            {
                sel.Select(obj, additive: false);
                _anchorIndex = _flat.IndexOf(obj);
            }
        }

        private void DrawDragDrop(EditorContext ctx, SceneObject obj)
        {
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                _dragObject = obj;
                ImGui.SetDragDropPayload("OUTLINER_NODE", IntPtr.Zero, 0);
                ImGui.Text(obj.Name);
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("OUTLINER_NODE");
                if (payload.NativePtr != null && _dragObject != null && _dragObject != obj && !IsAncestor(_dragObject, obj))
                {
                    obj.AttachChild(_dragObject);
                    ctx.Console?.AddLog(LogLevel.Info, $"已将 '{_dragObject.Name}' 附加到 '{obj.Name}'", "Outliner");
                    _dragObject = null;
                }
                ImGui.EndDragDropTarget();
            }
        }

        // 判断 candidate 是否为 obj 的祖先 (防止循环父子)
        private static bool IsAncestor(SceneObject candidate, SceneObject obj)
        {
            var p = obj.Parent;
            while (p != null)
            {
                if (p == candidate) return true;
                p = p.Parent;
            }
            return false;
        }

        private void DrawContextMenu(EditorContext ctx, SceneObject obj)
        {
            if (ImGui.BeginPopupContextItem("##OutlinerCtx"))
            {
                if (ImGui.BeginMenu("创建"))
                {
                    DrawCreateMenu(ctx, obj);
                    ImGui.EndMenu();
                }
                if (ImGui.MenuItem("重命名", "F2")) { _renameTarget = obj; _renameText = obj.Name; }
                if (ImGui.MenuItem("复制", "Ctrl+D")) ctx.World.Duplicate();
                if (ImGui.MenuItem("删除", "Del")) ctx.World.Delete();
                ImGui.Separator();
                if (ImGui.MenuItem("设为根对象")) obj.DetachFromParent();
                if (ImGui.MenuItem("隔离选中", null, false, ctx.Selection.Count > 0))
                {
                    foreach (var o in ctx.Assets.SceneObjects)
                        if (!o.IsSelected) o.IsVisible = false;
                }
                ImGui.EndPopup();
            }
        }

        private void DrawCreateMenu(EditorContext ctx, SceneObject parent)
        {
            if (ImGui.MenuItem("空对象")) Create(ctx, "Empty", "Empty", parent);
            ImGui.Separator();
            if (ImGui.BeginMenu("几何体"))
            {
                if (ImGui.MenuItem("立方体")) Create(ctx, "Cube", "Box", parent);
                if (ImGui.MenuItem("球体")) Create(ctx, "Sphere", "Sphere", parent);
                if (ImGui.MenuItem("平面")) Create(ctx, "Plane", "Plane", parent);
                if (ImGui.MenuItem("圆柱")) Create(ctx, "Cylinder", "Cylinder", parent);
                if (ImGui.MenuItem("圆环")) Create(ctx, "Torus", "Torus", parent);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("灯光"))
            {
                if (ImGui.MenuItem("方向光")) CreateLight(ctx, "Directional", parent);
                if (ImGui.MenuItem("点光源")) CreateLight(ctx, "Point", parent);
                if (ImGui.MenuItem("聚光灯")) CreateLight(ctx, "Spot", parent);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("相机")) CreateCamera(ctx, parent);
        }

        private void Create(EditorContext ctx, string name, string meshType, SceneObject parent)
        {
            var pos = parent?.Position ?? Vector3.Zero;
            var obj = ctx.World.SceneManager.CreateObject(name, meshType, pos);
            if (obj != null && parent != null) parent.AttachChild(obj);
            if (obj != null) ctx.Selection.Select(obj);
        }

        private void CreateLight(EditorContext ctx, string type, SceneObject parent)
        {
            var pos = parent?.Position ?? new Vector3(5, 10, 5);
            var obj = ctx.Assets.CreateLight(type, pos);
            if (obj != null && parent != null) parent.AttachChild(obj);
            if (obj != null) ctx.Selection.Select(obj);
        }

        private void CreateCamera(EditorContext ctx, SceneObject parent)
        {
            var pos = parent?.Position ?? new Vector3(0, 2, 8);
            var obj = ctx.Assets.CreateCamera(pos);
            if (obj != null && parent != null) parent.AttachChild(obj);
            if (obj != null) ctx.Selection.Select(obj);
        }

        private static (string icon, Vector4 color) GetTypeIcon(SceneObject obj)
        {
            var mesh = obj.MeshAssetPath ?? string.Empty;
            if (mesh.StartsWith("Light_")) return ("L", new Vector4(1.0f, 0.85f, 0.2f, 1.0f));
            if (mesh.StartsWith("Camera_")) return ("C", new Vector4(0.3f, 0.85f, 0.95f, 1.0f));
            if (string.IsNullOrEmpty(mesh) || mesh == "Empty") return ("O", new Vector4(0.6f, 0.6f, 0.62f, 1.0f));
            return ("M", new Vector4(0.45f, 0.8f, 0.5f, 1.0f));
        }
    }
}
