using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 快速操作栏 (QuickActionsBar)
    //
    // 快速操作栏 UI 组件:
    //   * 常用操作按钮 (保存/撤销/重做/复制/粘贴/删除/成组/对齐/分布)
    //   * 对齐工具 (左/右/上/下/中/等间距)
    //   * 分布工具 (水平/垂直均匀分布)
    //   * 变换对齐 (吸附到地面/对齐到法线/对齐到摄像机)
    //   * 快捷搜索 (命令面板/快捷键查找)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 快速操作
    // ──────────────────────────────────────────────────────────────

    /// <summary>快速操作类型。</summary>
    public enum QuickAction
    {
        Save,
        SaveAs,
        Undo,
        Redo,
        Copy,
        Paste,
        Cut,
        Delete,
        Duplicate,
        Group,
        Ungroup,
        SelectAll,
        DeselectAll,
        FrameSelection,
        Play,
        Pause,
        Stop,
        ToggleGizmo
    }

    /// <summary>快速操作按钮。</summary>
    public sealed class QuickActionButton
    {
        /// <summary>操作类型。</summary>
        public QuickAction Action { get; }
        /// <summary>显示文本。</summary>
        public string Label { get; }
        /// <summary>工具提示。</summary>
        public string Tooltip { get; set; } = string.Empty;
        /// <summary>快捷键 (显示用)。</summary>
        public string Shortcut { get; set; } = string.Empty;
        /// <summary>是否可用。</summary>
        public bool IsEnabled { get; set; } = true;
        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>点击回调。</summary>
        public event Action OnClick;

        public QuickActionButton(QuickAction action, string label)
        {
            Action = action;
            Label = label;
        }

        /// <summary>触发点击。</summary>
        public void Click() => OnClick?.Invoke();
    }

    /// <summary>快速操作栏。</summary>
    public sealed class QuickActionsBar
    {
        /// <summary>所有操作按钮。</summary>
        public readonly List<QuickActionButton> Buttons = new();

        /// <summary>操作执行事件 (统一出口, 供编辑器主循环消费)。</summary>
        public event Action<QuickAction> OnActionExecuted;

        /// <summary>初始化默认按钮。</summary>
        public void InitializeDefaults()
        {
            Buttons.Clear();
            AddButton(QuickAction.Save, "Save", "Ctrl+S");
            AddButton(QuickAction.Undo, "Undo", "Ctrl+Z");
            AddButton(QuickAction.Redo, "Redo", "Ctrl+Y");
            AddButton(QuickAction.Copy, "Copy", "Ctrl+C");
            AddButton(QuickAction.Paste, "Paste", "Ctrl+V");
            AddButton(QuickAction.Cut, "Cut", "Ctrl+X");
            AddButton(QuickAction.Delete, "Delete", "Del");
            AddButton(QuickAction.Duplicate, "Duplicate", "Ctrl+D");
            AddButton(QuickAction.Group, "Group", "Ctrl+G");
            AddButton(QuickAction.Ungroup, "Ungroup", "Ctrl+Shift+G");
            AddButton(QuickAction.FrameSelection, "Frame", "F");
        }

        private void AddButton(QuickAction action, string label, string shortcut = "")
        {
            var btn = new QuickActionButton(action, label) { Shortcut = shortcut };
            btn.OnClick += () => OnActionExecuted?.Invoke(action);
            Buttons.Add(btn);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 对齐工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>对齐轴向。</summary>
    public enum AlignAxis
    {
        MinX, MaxX, CenterX,
        MinY, MaxY, CenterY,
        MinZ, MaxZ, CenterZ
    }

    /// <summary>对齐工具: 对选中对象执行对齐操作。</summary>
    public static class AlignTools
    {
        /// <summary>沿 X 轴对齐 (最小/最大/中心)。</summary>
        public static void AlignX(List<Vector3> positions, AlignAxis mode)
        {
            if (positions == null || positions.Count < 2) return;
            float target = CalculateAlignTarget(positions, mode, p => p.X);
            for (int i = 0; i < positions.Count; i++)
            {
                positions[i] = new Vector3(target, positions[i].Y, positions[i].Z);
            }
        }

        /// <summary>沿 Y 轴对齐。</summary>
        public static void AlignY(List<Vector3> positions, AlignAxis mode)
        {
            if (positions == null || positions.Count < 2) return;
            float target = CalculateAlignTarget(positions, mode, p => p.Y);
            for (int i = 0; i < positions.Count; i++)
            {
                positions[i] = new Vector3(positions[i].X, target, positions[i].Z);
            }
        }

        /// <summary>沿 Z 轴对齐。</summary>
        public static void AlignZ(List<Vector3> positions, AlignAxis mode)
        {
            if (positions == null || positions.Count < 2) return;
            float target = CalculateAlignTarget(positions, mode, p => p.Z);
            for (int i = 0; i < positions.Count; i++)
            {
                positions[i] = new Vector3(positions[i].X, positions[i].Y, target);
            }
        }

        private static float CalculateAlignTarget(List<Vector3> positions, AlignAxis mode, Func<Vector3, float> accessor)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in positions)
            {
                float v = accessor(p);
                if (v < min) min = v;
                if (v > max) max = v;
            }

            return mode switch
            {
                AlignAxis.MinX or AlignAxis.MinY or AlignAxis.MinZ => min,
                AlignAxis.MaxX or AlignAxis.MaxY or AlignAxis.MaxZ => max,
                _ => (min + max) * 0.5f
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 分布工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>分布方向。</summary>
    public enum DistributeDirection
    {
        /// <summary>水平 (X 轴)。</summary>
        Horizontal,
        /// <summary>垂直 (Y 轴)。</summary>
        Vertical,
        /// <summary>深度 (Z 轴)。</summary>
        Depth
    }

    /// <summary>分布工具: 均匀分布选中对象。</summary>
    public static class DistributeTools
    {
        /// <summary>均匀分布选中对象。</summary>
        public static void Distribute(List<Vector3> positions, DistributeDirection direction)
        {
            if (positions == null || positions.Count < 3) return;

            Func<Vector3, float> accessor = direction switch
            {
                DistributeDirection.Horizontal => p => p.X,
                DistributeDirection.Vertical => p => p.Y,
                DistributeDirection.Depth => p => p.Z,
                _ => p => p.X
            };

            Action<Vector3, float, int> setter = direction switch
            {
                DistributeDirection.Horizontal => (p, v, i) => positions[i] = new Vector3(v, p.Y, p.Z),
                DistributeDirection.Vertical => (p, v, i) => positions[i] = new Vector3(p.X, v, p.Z),
                DistributeDirection.Depth => (p, v, i) => positions[i] = new Vector3(p.X, p.Y, v),
                _ => (p, v, i) => positions[i] = new Vector3(v, p.Y, p.Z)
            };

            // 按排序位置
            var sorted = new List<(float val, int idx)>();
            for (int i = 0; i < positions.Count; i++)
                sorted.Add((accessor(positions[i]), i));
            sorted.Sort((a, b) => a.val.CompareTo(b.val));

            float min = sorted[0].val;
            float max = sorted[sorted.Count - 1].val;
            float step = sorted.Count > 1 ? (max - min) / (sorted.Count - 1) : 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                float newValue = min + step * i;
                int idx = sorted[i].idx;
                setter(positions[idx], newValue, idx);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 变换对齐
    // ──────────────────────────────────────────────────────────────

    /// <summary>变换对齐类型。</summary>
    public enum TransformAlignType
    {
        /// <summary>吸附到地面 (Y=0)。</summary>
        SnapToGround,
        /// <summary>对齐到表面法线。</summary>
        AlignToNormal,
        /// <summary>对齐到摄像机朝向。</summary>
        AlignToCamera
    }

    /// <summary>变换对齐工具。</summary>
    public static class TransformAlignTools
    {
        /// <summary>将对象吸附到地面。</summary>
        public static Vector3 SnapToGround(Vector3 position, float groundY = 0f)
        {
            return new Vector3(position.X, groundY, position.Z);
        }

        /// <summary>对齐旋转到表面法线。</summary>
        public static Quaternion AlignToNormal(Vector3 normal)
        {
            if (normal == Vector3.Zero) return Quaternion.Identity;
            normal = Vector3.Normalize(normal);

            // 构建旋转: 使对象的 Up 轴对齐到法线
            Vector3 up = Vector3.UnitY;
            float dot = Vector3.Dot(up, normal);
            if (MathF.Abs(dot) > 0.9999f)
                return dot > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

            Vector3 axis = Vector3.Normalize(Vector3.Cross(up, normal));
            float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            return Quaternion.CreateFromAxisAngle(axis, angle);
        }

        /// <summary>对齐对象的 Z 轴朝向摄像机。</summary>
        public static Quaternion AlignToCamera(Vector3 objectPosition, Vector3 cameraPosition)
        {
            Vector3 forward = Vector3.Normalize(cameraPosition - objectPosition);
            return Quaternion.CreateFromYawPitchRoll(
                MathF.Atan2(forward.X, forward.Z),
                -MathF.Asin(forward.Y),
                0f);
        }

        /// <summary>对齐到目标变换。</summary>
        public static void AlignToTransform(
            ref Vector3 position, ref Quaternion rotation,
            Vector3 targetPosition, Quaternion targetRotation,
            TransformAlignType alignType, Vector3? surfaceNormal = null)
        {
            switch (alignType)
            {
                case TransformAlignType.SnapToGround:
                    position = SnapToGround(position);
                    break;

                case TransformAlignType.AlignToNormal:
                    if (surfaceNormal.HasValue)
                        rotation = AlignToNormal(surfaceNormal.Value);
                    break;

                case TransformAlignType.AlignToCamera:
                    rotation = AlignToCamera(position, targetPosition);
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 快捷搜索 / 命令面板
    // ──────────────────────────────────────────────────────────────

    /// <summary>命令条目。</summary>
    public sealed class CommandEntry
    {
        /// <summary>命令 ID。</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        /// <summary>命令名称。</summary>
        public string Name { get; set; }
        /// <summary>命令描述。</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>快捷键。</summary>
        public string Shortcut { get; set; } = string.Empty;
        /// <summary>分类标签。</summary>
        public string Category { get; set; } = string.Empty;
        /// <summary>命令执行回调。</summary>
        public event Action OnExecute;

        /// <summary>执行命令。</summary>
        public void Execute() => OnExecute?.Invoke();
    }

    /// <summary>命令面板: 快捷搜索和执行命令。</summary>
    public sealed class CommandPalette
    {
        /// <summary>所有已注册命令。</summary>
        private readonly List<CommandEntry> _commands = new();

        /// <summary>命令注册变更事件。</summary>
        public event Action OnCommandsChanged;

        /// <summary>是否打开。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>搜索查询。</summary>
        public string SearchQuery { get; set; } = string.Empty;

        /// <summary>命令执行前事件。</summary>
        public event Action<CommandEntry> OnBeforeExecute;

        /// <summary>注册命令。</summary>
        public void RegisterCommand(CommandEntry command)
        {
            _commands.Add(command ?? throw new ArgumentNullException(nameof(command)));
            OnCommandsChanged?.Invoke();
        }

        /// <summary>搜索命令 (按名称/描述/快捷键/分类)。</summary>
        public List<CommandEntry> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<CommandEntry>(_commands);

            query = query.ToLowerInvariant();
            var results = new List<CommandEntry>();
            foreach (var cmd in _commands)
            {
                if (cmd.Name.ToLowerInvariant().Contains(query) ||
                    cmd.Description.ToLowerInvariant().Contains(query) ||
                    cmd.Shortcut.ToLowerInvariant().Contains(query) ||
                    cmd.Category.ToLowerInvariant().Contains(query))
                {
                    results.Add(cmd);
                }
            }
            return results;
        }

        /// <summary>执行命令。</summary>
        public bool ExecuteCommand(string commandId)
        {
            var cmd = _commands.Find(c => c.Id == commandId);
            if (cmd == null) return false;
            OnBeforeExecute?.Invoke(cmd);
            cmd.Execute();
            return true;
        }

        /// <summary>按快捷键查找命令。</summary>
        public CommandEntry FindByShortcut(string shortcut)
        {
            return _commands.Find(c =>
                string.Equals(c.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>打开面板。</summary>
        public void Open()
        {
            IsOpen = true;
            SearchQuery = string.Empty;
        }

        /// <summary>关闭面板。</summary>
        public void Close() => IsOpen = false;
    }

    // ──────────────────────────────────────────────────────────────
    // 快速操作栏 (总装)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 快速操作栏管理器: 聚合操作按钮/对齐/分布/变换对齐/命令面板。
    /// </summary>
    public sealed class QuickActionsBarManager
    {
        /// <summary>操作按钮栏。</summary>
        public QuickActionsBar ActionsBar { get; } = new();
        /// <summary>命令面板。</summary>
        public CommandPalette CommandPalette { get; } = new();

        /// <summary>初始化默认操作和命令。</summary>
        public void Initialize()
        {
            ActionsBar.InitializeDefaults();
            RegisterDefaultCommands();
        }

        private void RegisterDefaultCommands()
        {
            // 文件操作
            RegisterCmd("save", "Save Scene", "Save the current scene", "Ctrl+S", "File");
            RegisterCmd("save_as", "Save As", "Save scene to a new file", "Ctrl+Shift+S", "File");
            RegisterCmd("new_scene", "New Scene", "Create a new empty scene", "Ctrl+N", "File");

            // 编辑操作
            RegisterCmd("undo", "Undo", "Undo last action", "Ctrl+Z", "Edit");
            RegisterCmd("redo", "Redo", "Redo last action", "Ctrl+Y", "Edit");
            RegisterCmd("delete", "Delete", "Delete selected objects", "Delete", "Edit");
            RegisterCmd("duplicate", "Duplicate", "Duplicate selected objects", "Ctrl+D", "Edit");
            RegisterCmd("group", "Group", "Group selected objects", "Ctrl+G", "Edit");

            // 视图操作
            RegisterCmd("frame", "Frame Selection", "Focus camera on selection", "F", "View");
            RegisterCmd("top_view", "Top View", "Switch to top orthographic view", "Numpad 7", "View");
            RegisterCmd("front_view", "Front View", "Switch to front orthographic view", "Numpad 1", "View");
            RegisterCmd("right_view", "Right View", "Switch to right orthographic view", "Numpad 3", "View");

            // 播放操作
            RegisterCmd("play", "Play", "Start Play-In-Editor", "Ctrl+P", "Play");
            RegisterCmd("stop", "Stop", "Stop PIE", "Escape", "Play");

            // 对齐操作
            RegisterCmd("align_left", "Align Left", "Align selected objects left edge", "", "Align");
            RegisterCmd("align_center", "Align Center", "Align selected objects center", "", "Align");
            RegisterCmd("align_right", "Align Right", "Align selected objects right edge", "", "Align");

            // 搜索操作
            RegisterCmd("find_command", "Find Command", "Open command palette", "Ctrl+Shift+P", "Search");
        }

        private void RegisterCmd(string id, string name, string desc, string shortcut, string category)
        {
            CommandPalette.RegisterCommand(new CommandEntry
            {
                Name = name,
                Description = desc,
                Shortcut = shortcut,
                Category = category
            });
        }

        /// <summary>处理快捷键, 返回是否被消费。</summary>
        public bool HandleShortcut(string shortcut)
        {
            var cmd = CommandPalette.FindByShortcut(shortcut);
            if (cmd != null)
            {
                cmd.Execute();
                return true;
            }
            return false;
        }
    }
}
