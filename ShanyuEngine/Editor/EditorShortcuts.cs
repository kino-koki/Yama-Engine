using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Input;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 编辑器快捷键系统 (EditorShortcuts)
    //
    // 功能:
    //   - 注册 / 注销快捷键 (主键 + Ctrl / Shift / Alt 修饰键)
    //   - 每帧轮询 InputSystem 检测触发
    //   - 文本输入时自动屏蔽非修饰键快捷键 (避免 W/E/R 误触)
    //   - 默认快捷键集 (RegisterDefaults)
    //   - 快捷键列表查询 (供 Help 菜单显示)
    //
    // 默认快捷键:
    //   Ctrl+S       保存场景
    //   Ctrl+Z       撤销
    //   Ctrl+Y       重做 (Ctrl+Shift+Z 同效)
    //   Ctrl+D       复制选中
    //   Delete       删除选中
    //   W            移动模式
    //   E            旋转模式
    //   R            缩放模式
    //   F            聚焦选中
    //   Ctrl+Shift+N 新建实体
    //   Ctrl+P       播放/停止 PIE
    //   Ctrl+B       构建项目
    //   Ctrl+F       搜索大纲
    //   Ctrl+A       全选
    //   Ctrl+C       复制到剪贴板
    //   Ctrl+V       粘贴
    //   Ctrl+X       剪切
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器快捷键定义。</summary>
    public readonly struct EditorShortcut
    {
        public readonly InputKey Key;
        public readonly bool Ctrl;
        public readonly bool Shift;
        public readonly bool Alt;
        public readonly string Description;
        public readonly string Category;
        public readonly Action<EditorContext> Handler;

        public EditorShortcut(InputKey key, bool ctrl, bool shift, bool alt,
                              string description, string category, Action<EditorContext> handler)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
            Description = description ?? "";
            Category = category ?? "通用";
            Handler = handler;
        }

        /// <summary>是否有修饰键 (Ctrl / Shift / Alt)。</summary>
        public bool HasModifier => Ctrl || Shift || Alt;

        /// <summary>格式化为可读字符串。</summary>
        public string DisplayString
        {
            get
            {
                var parts = new List<string>();
                if (Ctrl) parts.Add("Ctrl");
                if (Shift) parts.Add("Shift");
                if (Alt) parts.Add("Alt");
                parts.Add(KeyToString(Key));
                return string.Join("+", parts);
            }
        }

        public bool Matches(InputSystem input)
        {
            if (input == null) return false;
            // 主键必须在本帧刚刚按下 (边沿触发)
            if (!input.IsKeyPressed(Key)) return false;
            // 修饰键必须处于持续按下状态
            if (Ctrl != input.IsKeyDown(InputKey.Control)) return false;
            if (Shift != input.IsKeyDown(InputKey.Shift)) return false;
            if (Alt != input.IsKeyDown(InputKey.Alt)) return false;
            return true;
        }

        private static string KeyToString(InputKey key)
        {
            return key switch
            {
                InputKey.Delete => "Del",
                InputKey.Enter => "Enter",
                InputKey.Escape => "Esc",
                InputKey.Space => "Space",
                InputKey.Tab => "Tab",
                InputKey.LeftArrow => "Left",
                InputKey.RightArrow => "Right",
                InputKey.UpArrow => "Up",
                InputKey.DownArrow => "Down",
                InputKey.MouseLeft => "LMB",
                InputKey.MouseRight => "RMB",
                InputKey.MouseMiddle => "MMB",
                _ => key.ToString()
            };
        }
    }

    /// <summary>快捷键管理器: 注册 / 检测 / 查询快捷键。</summary>
    public sealed class EditorShortcutManager
    {
        private readonly List<EditorShortcut> _shortcuts = new();
        private EditorContext _ctx;

        /// <summary>上次触发的快捷键描述 (供状态栏显示)。</summary>
        public string LastTriggered { get; private set; } = "";

        /// <summary>所有已注册的快捷键。</summary>
        public IReadOnlyList<EditorShortcut> Shortcuts => _shortcuts;

        /// <summary>初始化并注册默认快捷键。</summary>
        public void Initialize(EditorContext ctx)
        {
            _ctx = ctx;
            RegisterDefaults(ctx);
        }

        // ─────────────────────────── 注册 / 注销 ───────────────────────────

        public void Register(InputKey key, bool ctrl, bool shift, bool alt,
                             string description, string category, Action<EditorContext> handler)
        {
            if (handler == null) return;
            // 避免重复注册完全相同的快捷键
            var existing = _shortcuts.FirstOrDefault(s =>
                s.Key == key && s.Ctrl == ctrl && s.Shift == shift && s.Alt == alt);
            if (existing.Handler != null)
                _shortcuts.Remove(existing);

            _shortcuts.Add(new EditorShortcut(key, ctrl, shift, alt, description, category, handler));
        }

        public void Register(InputKey key, string description, Action<EditorContext> handler)
            => Register(key, false, false, false, description, "通用", handler);

        public bool Unregister(InputKey key, bool ctrl, bool shift, bool alt)
        {
            int removed = _shortcuts.RemoveAll(s =>
                s.Key == key && s.Ctrl == ctrl && s.Shift == shift && s.Alt == alt);
            return removed > 0;
        }

        public void Clear() => _shortcuts.Clear();

        // ─────────────────────────── 每帧更新 ───────────────────────────

        /// <summary>每帧检测并触发快捷键 (由 MainEditorWindow 调用)。</summary>
        public void Update(EditorContext ctx)
        {
            if (ctx?.Input == null) return;

            // 文本输入活跃时, 仅处理带修饰键的快捷键
            bool wantText = false;
            try { wantText = ImGui.GetIO().WantTextInput; }
            catch { /* ImGui 未初始化时忽略 */ }

            foreach (var sc in _shortcuts)
            {
                // 无修饰键快捷键在文本输入时跳过
                if (wantText && !sc.HasModifier) continue;

                if (sc.Matches(ctx.Input))
                {
                    try
                    {
                        sc.Handler?.Invoke(ctx);
                        LastTriggered = sc.DisplayString;
                    }
                    catch (Exception ex)
                    {
                        ctx.Console?.AddLog(LogLevel.Error, $"快捷键 {sc.DisplayString} 执行失败: {ex.Message}", "shortcut");
                    }
                    break; // 一帧只触发一个快捷键
                }
            }
        }

        // ─────────────────────────── 默认快捷键 ───────────────────────────

        /// <summary>注册引擎默认快捷键集。</summary>
        public void RegisterDefaults(EditorContext ctx)
        {
            // ── 文件 ──
            Register(InputKey.S, true, false, false, "保存场景", "文件",
                c => { c.SceneManager?.SaveScene(); c.Console?.AddLog(LogLevel.Info, "场景已保存", "file"); });

            Register(InputKey.N, true, true, false, "新建实体", "文件",
                c =>
                {
                    var obj = c.SceneManager?.CreateObject("GameObject", "Box", Vector3.Zero);
                    if (obj != null) c.Selection?.Select(obj);
                });

            // ── 编辑 ──
            Register(InputKey.Z, true, false, false, "撤销", "编辑",
                c => { if (c.World?.Undo() == true) c.Console?.AddLog(LogLevel.Debug, $"撤销: {c.Undo?.UndoDescription}", "undo"); });

            Register(InputKey.Y, true, false, false, "重做", "编辑",
                c => { if (c.World?.Redo() == true) c.Console?.AddLog(LogLevel.Debug, $"重做: {c.Undo?.RedoDescription}", "redo"); });

            // Ctrl+Shift+Z 也作为重做 (常见习惯)
            Register(InputKey.Z, true, true, false, "重做 (Ctrl+Shift+Z)", "编辑",
                c => { if (c.World?.Redo() == true) c.Console?.AddLog(LogLevel.Debug, $"重做: {c.Undo?.RedoDescription}", "redo"); });

            Register(InputKey.D, true, false, false, "复制选中", "编辑",
                c => c.World?.Duplicate());

            Register(InputKey.Delete, "删除选中", "编辑",
                c => c.World?.Delete());

            Register(InputKey.A, true, false, false, "全选", "编辑",
                c => c.World?.SelectAll());

            Register(InputKey.C, true, false, false, "复制到剪贴板", "编辑",
                c => c.World?.Copy());

            Register(InputKey.V, true, false, false, "粘贴", "编辑",
                c => c.World?.Paste());

            Register(InputKey.X, true, false, false, "剪切", "编辑",
                c => c.World?.Cut());

            // ── 变换模式 ──
            Register(InputKey.W, "移动模式", "变换",
                c => SetGizmoMode(c, GizmoMode.Move));

            Register(InputKey.E, "旋转模式", "变换",
                c => SetGizmoMode(c, GizmoMode.Rotate));

            Register(InputKey.R, "缩放模式", "变换",
                c => SetGizmoMode(c, GizmoMode.Scale));

            // ── 视口 ──
            Register(InputKey.F, "聚焦选中", "视口",
                c =>
                {
                    c.World?.FrameSelection();
                    c.Editor?.Viewport?.FrameSelection();
                });

            // ── PIE ──
            Register(InputKey.P, true, false, false, "播放/停止 PIE", "运行",
                c =>
                {
                    if (c.Pie == null) return;
                    if (c.Pie.IsPlaying || c.Pie.IsPaused) c.World?.Stop();
                    else c.World?.Play();
                });

            Register(InputKey.Escape, "停止 PIE", "运行",
                c => { if (c.Pie?.IsPlaying == true || c.Pie?.IsPaused == true) c.World?.Stop(); });

            // ── 构建 ──
            Register(InputKey.B, true, false, false, "构建项目", "构建",
                c => c.Console?.AddLog(LogLevel.Info, "构建项目... (占位)", "build"));

            // ── 其他 ──
            Register(InputKey.F1, true, false, false, "切换统计覆盖层", "视图",
                c =>
                {
                    if (c.Editor?.Toolbar != null)
                        c.Editor.Toolbar.ShowStatsOverlay = !c.Editor.Toolbar.ShowStatsOverlay;
                });

            Register(InputKey.G, true, false, false, "切换网格", "视图",
                c =>
                {
                    if (c.Editor?.Viewport != null)
                        c.Editor.Viewport.Overlay.ShowGrid = !c.Editor.Viewport.Overlay.ShowGrid;
                });

            Register(InputKey.O, true, false, false, "切换正交/透视", "视图",
                c =>
                {
                    if (c.Editor?.Viewport != null)
                    {
                        var cam = c.Editor.Viewport.Camera;
                        cam.Mode = cam.Mode == ViewportCameraMode.Orthographic
                            ? ViewportCameraMode.Orbit
                            : ViewportCameraMode.Orthographic;
                    }
                });

            Register(InputKey.T, true, false, false, "切换世界/本地坐标", "视图",
                c =>
                {
                    if (c.Editor?.Viewport != null)
                        c.Editor.Viewport.Gizmo.UseWorldSpace = !c.Editor.Viewport.Gizmo.UseWorldSpace;
                });
        }

        // ─────────────────────────── 辅助 ───────────────────────────

        private static void SetGizmoMode(EditorContext ctx, GizmoMode mode)
        {
            if (ctx?.Editor?.Viewport != null)
                ctx.Editor.Viewport.Gizmo.Mode = mode;
        }

        /// <summary>按类别获取快捷键列表 (供 Help 菜单使用)。</summary>
        public IEnumerable<IGrouping<string, EditorShortcut>> GetByCategory()
        {
            return _shortcuts.GroupBy(s => s.Category).OrderBy(g => g.Key);
        }
    }
}
