// ==================================================================================================
//  EcsHierarchyPanel.cs — yama引擎 ECS 层级面板
// ==================================================================================================
//  命名空间: YamaEngine.Editor.Panels
//  依赖:     ImGuiNET, YamaEngine.ECS
//
//  功能:
//    以 ImGui 树形列表显示 ECS 中所有实体，支持:
//      * 父子层级展开/折叠（基于 RelationshipComponent）
//      * 单击选中 / Ctrl 多选 / Shift 范围选
//      * 右键菜单：创建实体 / 删除 / 复制 / 重命名 / 添加组件
//      * 搜索过滤
//      * 实体类型图标（Mesh/Light/Camera/Empty）
//      * 可见性切换
//      * 拖拽重排父子关系
//
//  与 EcsSelectionState 联动，选中后 EcsInspectorPanel 自动刷新。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using YamaEngine.ECS;

namespace YamaEngine.Editor.Panels
{
    /// <summary>
    /// ECS 层级面板。以树形结构显示和编辑 ECS 中的所有实体。
    /// </summary>
    public sealed class EcsHierarchyPanel
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;

        // ── UI 状态 ────────────────────────────────────────────────────────────────

        private string _search = string.Empty;
        private bool _expandAll;
        private bool _collapseAll;
        private int _renameId = -1;
        private string _renameText = string.Empty;
        private int _dragEntityId = -1;
        private readonly List<int> _flatList = new();

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>实体被创建后触发（参数为实体 ID）。</summary>
        public event Action<int>? OnEntityCreated;

        /// <summary>实体被删除后触发。</summary>
        public event Action<int>? OnEntityDeleted;

        /// <summary>场景被修改（需要标记 Dirty）。</summary>
        public event Action? OnSceneModified;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>窗口标题。</summary>
        public string Title { get; set; } = "ECS 层级";

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public EcsHierarchyPanel(EntityComponentSystem ecs, EcsSelectionState selection)
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

            ImGui.SetNextWindowSizeConstraints(new Vector2(200, 250), new Vector2(float.MaxValue, float.MaxValue));
            if (!ImGui.Begin($"{Title}###EcsHierarchy"))
            {
                ImGui.End();
                return;
            }

            DrawToolbar();
            ImGui.Separator();

            ImGui.BeginChild("##EcsHierarchyTree", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);

            _flatList.Clear();

            if (!string.IsNullOrWhiteSpace(_search))
            {
                // 搜索模式：扁平显示
                foreach (int id in _ecs.AllEntities)
                {
                    string name = _ecs.GetEntityName(id) ?? "Entity";
                    if (!name.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
                    _flatList.Add(id);
                    DrawEntityRow(id, 0);
                }
            }
            else
            {
                // 层级模式：仅显示根节点
                foreach (int id in _ecs.AllEntities)
                {
                    if (!_ecs.HasComponent<RelationshipComponent>(id))
                    {
                        _flatList.Add(id);
                        DrawEntityRecursive(id, 0);
                        continue;
                    }

                    var rel = _ecs.GetComponent<RelationshipComponent>(id);
                    if (rel.ParentId == 0 || !_ecs.HasEntity(rel.ParentId))
                    {
                        _flatList.Add(id);
                        DrawEntityRecursive(id, 0);
                    }
                }
            }

            // 空白处右键菜单
            if (ImGui.BeginPopupContextWindow("##EcsHierarchyBg",
                ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
            {
                DrawCreateMenu(-1);
                ImGui.EndPopup();
            }

            // 空白处作为拖放目标 → 设为根
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ECS_ENTITY");
                    if (payload.NativePtr != null && _dragEntityId >= 0)
                    {
                        DetachFromParent(_dragEntityId);
                        _dragEntityId = -1;
                        OnSceneModified?.Invoke();
                    }
                }
                ImGui.EndDragDropTarget();
            }

            ImGui.EndChild();

            _expandAll = false;
            _collapseAll = false;

            ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  工具栏
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            float avail = ImGui.GetContentRegionAvail().X;

            ImGui.PushItemWidth(avail - 150);
            ImGui.InputTextWithHint("##EcsHierarchySearch", "搜索实体...", ref _search, 128);
            ImGui.PopItemWidth();

            ImGui.SameLine();
            if (ImGui.Button("+ 创建")) ImGui.OpenPopup("##EcsCreatePopup");

            ImGui.SameLine();
            if (ImGui.Button("展开")) _expandAll = true;

            ImGui.SameLine();
            if (ImGui.Button("折叠")) _collapseAll = true;

            if (ImGui.BeginPopup("##EcsCreatePopup"))
            {
                DrawCreateMenu(-1);
                ImGui.EndPopup();
            }
        }

        private void DrawCreateMenu(int parentId)
        {
            if (ImGui.MenuItem("空实体"))
            {
                int id = CreateEntity("Entity", parentId);
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("网格实体"))
            {
                int id = CreateEntity("Mesh", parentId);
                _ecs.AddComponent(id, new MeshComponent { IsVisible = true, CastsShadow = true });
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
            }

            if (ImGui.MenuItem("方向光"))
            {
                int id = CreateEntity("DirectionalLight", parentId);
                _ecs.AddComponent(id, new LightComponent
                {
                    Type = LightComponent.LightType.Directional,
                    Color = new Vector3(1, 0.95f, 0.9f),
                    Intensity = 1.0f,
                });
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
            }

            if (ImGui.MenuItem("点光源"))
            {
                int id = CreateEntity("PointLight", parentId);
                _ecs.AddComponent(id, new LightComponent
                {
                    Type = LightComponent.LightType.Point,
                    Color = new Vector3(1, 1, 1),
                    Intensity = 2.0f,
                    Range = 10f,
                });
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
            }

            if (ImGui.MenuItem("相机"))
            {
                int id = CreateEntity("Camera", parentId);
                _ecs.AddComponent(id, new CameraComponent
                {
                    FieldOfView = 60f,
                    NearClip = 0.1f,
                    FarClip = 1000f,
                    AspectRatio = 16f / 9f,
                });
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  递归绘制实体
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawEntityRecursive(int entityId, int depth)
        {
            ImGui.PushID(entityId);

            string name = _ecs.GetEntityName(entityId) ?? "Entity";
            var (icon, iconColor) = GetEntityIcon(entityId);
            bool selected = _selection.IsSelected(entityId);

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                        ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
            if (selected) flags |= ImGuiTreeNodeFlags.Selected;

            // 检查是否有子实体
            List<int>? children = GetChildren(entityId);
            if (children == null || children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

            if (_expandAll) ImGui.SetNextItemOpen(true);
            if (_collapseAll) ImGui.SetNextItemOpen(false);

            // 重命名模式
            if (_renameId == entityId)
            {
                ImGui.SetNextItemOpen(true);
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 60);
                if (ImGui.InputText("##Rename", ref _renameText, 64,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (!string.IsNullOrWhiteSpace(_renameText))
                        _ecs.SetEntityName(entityId, _renameText);
                    _renameId = -1;
                    OnSceneModified?.Invoke();
                }
                ImGui.PopItemWidth();
                if (!ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _renameId = -1;
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, iconColor);
                ImGui.TextUnformatted(icon);
                ImGui.PopStyleColor();
                ImGui.SameLine(0, 4);

                bool open = ImGui.TreeNodeEx($"{name}##{entityId}", flags);
                HandleInteraction(entityId);
                DrawDragDrop(entityId);
                DrawContextMenu(entityId);

                if (open && children != null && children.Count > 0)
                {
                    foreach (int childId in children)
                    {
                        _flatList.Add(childId);
                        DrawEntityRecursive(childId, depth + 1);
                    }
                    ImGui.TreePop();
                }
            }

            ImGui.PopID();
        }

        private void DrawEntityRow(int entityId, int depth)
        {
            ImGui.PushID(entityId + 1000000);

            string name = _ecs.GetEntityName(entityId) ?? "Entity";
            var (icon, iconColor) = GetEntityIcon(entityId);
            bool selected = _selection.IsSelected(entityId);

            ImGui.PushStyleColor(ImGuiCol.Text, iconColor);
            ImGui.TextUnformatted(icon);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 4);

            if (ImGui.Selectable($"{name}##{entityId}", selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
            {
                HandleClick(entityId);
            }

            DrawDragDrop(entityId);
            DrawContextMenu(entityId);

            ImGui.PopID();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  交互
        // ─────────────────────────────────────────────────────────────────────────

        private void HandleInteraction(int entityId)
        {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                HandleClick(entityId);

            // 双击重命名
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _renameId = entityId;
                _renameText = _ecs.GetEntityName(entityId) ?? "Entity";
            }
        }

        private void HandleClick(int entityId)
        {
            bool ctrl = ImGui.GetIO().KeyCtrl;
            bool shift = ImGui.GetIO().KeyShift;

            if (ctrl)
            {
                _selection.SelectToggle(entityId);
            }
            else if (shift && _selection.HasSelection)
            {
                // 范围选
                int lastId = _selection.Active;
                int startIdx = _flatList.IndexOf(lastId);
                int endIdx = _flatList.IndexOf(entityId);
                if (startIdx >= 0 && endIdx >= 0)
                {
                    if (startIdx > endIdx) (startIdx, endIdx) = (endIdx, startIdx);
                    for (int i = startIdx; i <= endIdx; i++)
                        _selection.SelectAdd(_flatList[i]);
                }
            }
            else
            {
                _selection.Select(entityId);
            }
        }

        private void DrawDragDrop(int entityId)
        {
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                _dragEntityId = entityId;
                unsafe
                {
                    int* ptr = stackalloc int[1];
                    ptr[0] = entityId;
                    ImGui.SetDragDropPayload("ECS_ENTITY", (nint)ptr, sizeof(int));
                }
                ImGui.Text(_ecs.GetEntityName(entityId) ?? "Entity");
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ECS_ENTITY");
                    if (payload.NativePtr != null && _dragEntityId >= 0 && _dragEntityId != entityId)
                    {
                        SetParent(_dragEntityId, entityId);
                        _dragEntityId = -1;
                        OnSceneModified?.Invoke();
                    }
                }
                ImGui.EndDragDropTarget();
            }
        }

        private void DrawContextMenu(int entityId)
        {
            if (!ImGui.BeginPopupContextItem($"##EcsCtx{entityId}"))
                return;

            if (ImGui.MenuItem("重命名"))
            {
                _renameId = entityId;
                _renameText = _ecs.GetEntityName(entityId) ?? "Entity";
            }

            if (ImGui.MenuItem("复制"))
            {
                int newId = DuplicateEntity(entityId);
                if (newId >= 0)
                {
                    _selection.Select(newId);
                    OnEntityCreated?.Invoke(newId);
                    OnSceneModified?.Invoke();
                }
            }

            ImGui.Separator();

            if (ImGui.MenuItem("创建子实体"))
            {
                int childId = CreateEntity("Entity", entityId);
                _selection.Select(childId);
                OnEntityCreated?.Invoke(childId);
                OnSceneModified?.Invoke();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("设为根"))
            {
                DetachFromParent(entityId);
                OnSceneModified?.Invoke();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("删除", "Del"))
            {
                DeleteEntity(entityId);
                OnEntityDeleted?.Invoke(entityId);
                OnSceneModified?.Invoke();
            }

            ImGui.EndPopup();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  实体图标
        // ─────────────────────────────────────────────────────────────────────────

        private (string Icon, Vector4 Color) GetEntityIcon(int entityId)
        {
            if (_ecs.HasComponent<CameraComponent>(entityId))
                return ("[C]", new Vector4(0.4f, 0.8f, 1.0f, 1f));
            if (_ecs.HasComponent<LightComponent>(entityId))
                return ("[L]", new Vector4(1.0f, 0.8f, 0.3f, 1f));
            if (_ecs.HasComponent<MeshComponent>(entityId))
                return ("[M]", new Vector4(0.5f, 1.0f, 0.5f, 1f));
            return ("[E]", new Vector4(0.7f, 0.7f, 0.7f, 1f));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  父子关系管理
        // ─────────────────────────────────────────────────────────────────────────

        private List<int>? GetChildren(int entityId)
        {
            if (!_ecs.HasComponent<RelationshipComponent>(entityId)) return null;
            var rel = _ecs.GetComponent<RelationshipComponent>(entityId);
            return rel.Children;
        }

        private void SetParent(int childId, int parentId)
        {
            if (childId == parentId) return;
            if (IsAncestorOf(childId, parentId)) return; // 防止循环

            DetachFromParent(childId);

            // 添加到新父
            if (!_ecs.HasComponent<RelationshipComponent>(parentId))
                _ecs.AddComponent(parentId, new RelationshipComponent { Children = new List<int>() });

            var parentRel = _ecs.GetComponent<RelationshipComponent>(parentId);
            parentRel.Children ??= new List<int>();
            if (!parentRel.Children.Contains(childId))
                parentRel.Children.Add(childId);
            _ecs.SetComponent(parentId, parentRel);

            // 设置子实体的 ParentId
            if (!_ecs.HasComponent<RelationshipComponent>(childId))
                _ecs.AddComponent(childId, new RelationshipComponent { Children = new List<int>() });

            var childRel = _ecs.GetComponent<RelationshipComponent>(childId);
            childRel.ParentId = parentId;
            _ecs.SetComponent(childId, childRel);
        }

        private void DetachFromParent(int entityId)
        {
            if (!_ecs.HasComponent<RelationshipComponent>(entityId)) return;
            var rel = _ecs.GetComponent<RelationshipComponent>(entityId);
            int oldParent = rel.ParentId;
            if (oldParent == 0) return;

            rel.ParentId = 0;
            _ecs.SetComponent(entityId, rel);

            // 从父实体的 Children 列表中移除
            if (_ecs.HasComponent<RelationshipComponent>(oldParent))
            {
                var parentRel = _ecs.GetComponent<RelationshipComponent>(oldParent);
                parentRel.Children?.Remove(entityId);
                _ecs.SetComponent(oldParent, parentRel);
            }
        }

        private bool IsAncestorOf(int ancestorId, int descendantId)
        {
            int current = descendantId;
            while (current != 0 && _ecs.HasEntity(current))
            {
                if (!_ecs.HasComponent<RelationshipComponent>(current)) break;
                var rel = _ecs.GetComponent<RelationshipComponent>(current);
                if (rel.ParentId == ancestorId) return true;
                if (rel.ParentId == 0) break;
                current = rel.ParentId;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  实体 CRUD
        // ─────────────────────────────────────────────────────────────────────────

        private int CreateEntity(string name, int parentId)
        {
            int id = _ecs.CreateEntity(name,
                position: Vector3.Zero,
                rotation: Quaternion.Identity,
                scale: Vector3.One);

            if (parentId >= 0 && _ecs.HasEntity(parentId))
                SetParent(id, parentId);

            return id;
        }

        private int DuplicateEntity(int sourceId)
        {
            if (!_ecs.HasEntity(sourceId)) return -1;
            string name = _ecs.GetEntityName(sourceId) ?? "Entity";

            int newId = _ecs.CreateEntity(name + " Copy");
            if (_ecs.HasComponent<TransformComponent>(sourceId))
                _ecs.AddComponent(newId, _ecs.GetComponent<TransformComponent>(sourceId));
            if (_ecs.HasComponent<MeshComponent>(sourceId))
                _ecs.AddComponent(newId, _ecs.GetComponent<MeshComponent>(sourceId));
            if (_ecs.HasComponent<LightComponent>(sourceId))
                _ecs.AddComponent(newId, _ecs.GetComponent<LightComponent>(sourceId));
            if (_ecs.HasComponent<CameraComponent>(sourceId))
                _ecs.AddComponent(newId, _ecs.GetComponent<CameraComponent>(sourceId));
            if (_ecs.HasComponent<TagComponent>(sourceId))
                _ecs.AddComponent(newId, _ecs.GetComponent<TagComponent>(sourceId));

            return newId;
        }

        private void DeleteEntity(int entityId)
        {
            if (!_ecs.HasEntity(entityId)) return;

            // 先断开父子关系
            DetachFromParent(entityId);

            // 把子实体设为根
            if (_ecs.HasComponent<RelationshipComponent>(entityId))
            {
                var rel = _ecs.GetComponent<RelationshipComponent>(entityId);
                if (rel.Children != null)
                {
                    foreach (int childId in rel.Children.ToArray())
                        DetachFromParent(childId);
                }
            }

            // 从选择中移除
            if (_selection.IsSelected(entityId))
                _selection.SelectToggle(entityId);

            _ecs.DestroyEntity(entityId);
        }

        /// <summary>处理 Del 键删除选中实体。</summary>
        public void HandleKeyboardShortcuts()
        {
            if (!IsVisible) return;
            if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;

            if (ImGui.IsKeyPressed(ImGuiKey.Delete))
            {
                foreach (int id in _selection.Selected.ToArray())
                {
                    DeleteEntity(id);
                    OnEntityDeleted?.Invoke(id);
                }
                OnSceneModified?.Invoke();
            }

            if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D))
            {
                foreach (int id in _selection.Selected.ToArray())
                {
                    int newId = DuplicateEntity(id);
                    if (newId >= 0) OnEntityCreated?.Invoke(newId);
                }
                OnSceneModified?.Invoke();
            }
        }
    }
}
