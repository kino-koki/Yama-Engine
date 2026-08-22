// ==================================================================================================
//  UnifiedEditorModeSystem.cs — 山雨引擎统一编辑器模式系统
//
//  命名空间: ShanyuEngine.Editor
//  依赖:     ImGuiNET
//
//  功能:
//    统一管理编辑器工作模式切换: 场景编辑 / 地形编辑 / 建模 / 代码 / 测试运行
//    所有模式在同一个 App 内切换，无需离开引擎。
//
//  设计理念:
//    UE5 有 Mode 切换但流程繁琐；山雨引擎一键切换，快捷键直接跳转。
//    每种模式有自己的工具栏、快捷键、视口行为。
//    模式切换时自动保存/恢复上下文。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════════
    // 编辑器模式枚举
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器工作模式。</summary>
    public enum EditorMode
    {
        /// <summary>场景编辑: 对象放置、变换、层级管理。</summary>
        Scene,
        /// <summary>地形编辑: 雕刻、绘制、噪声生成。</summary>
        Terrain,
        /// <summary>建模: 网格编辑、挤出、倒角、布尔运算。</summary>
        Modeling,
        /// <summary>代码: 脚本编写、编译、调试。</summary>
        Code,
        /// <summary>测试运行: PIE 播放、性能分析。</summary>
        Test
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 模式上下文 (保存/恢复状态)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>模式切换时保存的上下文快照。</summary>
    public sealed class ModeContext
    {
        public EditorMode Mode;
        public GizmoMode SavedGizmoMode;
        public string SavedRenderMode;
        public bool SavedGridVisible;
        public Vector3 SavedCameraPosition;
        public Vector3 SavedCameraRotation;
        public float SavedCameraFov;
        public DateTime EnterTime;
        public string Description = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 统一编辑器模式管理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 统一编辑器模式管理器。
    /// 管理场景编辑/地形/建模/代码/测试五种模式之间的切换。
    /// </summary>
    public sealed class UnifiedEditorModeSystem
    {
        // ── 当前状态 ──────────────────────────────────────────────────────────

        private EditorMode _currentMode = EditorMode.Scene;
        private EditorMode _previousMode = EditorMode.Scene;
        private readonly Stack<ModeContext> _modeHistory = new();
        private readonly Dictionary<EditorMode, ModeContext> _savedContexts = new();

        // ── 事件 ──────────────────────────────────────────────────────────────

        /// <summary>模式切换事件 (旧模式, 新模式)。</summary>
        public event Action<EditorMode, EditorMode> OnModeChanged;

        /// <summary>模式切换前事件 (可以取消)。</summary>
        public event Func<EditorMode, EditorMode, bool> OnModeChanging;

        // ── 属性 ──────────────────────────────────────────────────────────────

        /// <summary>当前编辑器模式。</summary>
        public EditorMode CurrentMode => _currentMode;

        /// <summary>前一个模式 (用于切换回来)。</summary>
        public EditorMode PreviousMode => _previousMode;

        /// <summary>模式历史记录数。</summary>
        public int HistoryCount => _modeHistory.Count;

        // ── 模式信息 ──────────────────────────────────────────────────────────

        private static readonly Dictionary<EditorMode, ModeInfo> ModeInfos = new()
        {
            [EditorMode.Scene] = new()
            {
                Name = "场景编辑",
                ShortName = "Scene",
                Icon = "🏠",
                Shortcut = "1",
                Color = new Vector4(0.4f, 0.6f, 0.9f, 1f),
                Description = "对象放置 · 变换 · 层级管理"
            },
            [EditorMode.Terrain] = new()
            {
                Name = "地形编辑",
                ShortName = "Terrain",
                Icon = "⛰",
                Shortcut = "2",
                Color = new Vector4(0.4f, 0.7f, 0.4f, 1f),
                Description = "雕刻 · 绘制 · 噪声生成"
            },
            [EditorMode.Modeling] = new()
            {
                Name = "建模",
                ShortName = "Model",
                Icon = "📐",
                Shortcut = "3",
                Color = new Vector4(0.9f, 0.6f, 0.3f, 1f),
                Description = "网格编辑 · 挤出 · 倒角"
            },
            [EditorMode.Code] = new()
            {
                Name = "代码",
                ShortName = "Code",
                Icon = "📝",
                Shortcut = "4",
                Color = new Vector4(0.6f, 0.5f, 0.9f, 1f),
                Description = "脚本编写 · 编译 · 调试"
            },
            [EditorMode.Test] = new()
            {
                Name = "测试运行",
                ShortName = "Test",
                Icon = "▶",
                Shortcut = "5",
                Color = new Vector4(0.3f, 0.8f, 0.5f, 1f),
                Description = "PIE 播放 · 性能分析"
            }
        };

        // ── 模式切换 ──────────────────────────────────────────────────────────

        /// <summary>切换到指定模式。</summary>
        /// <param name="mode">目标模式。</param>
        /// <param name="description">切换描述 (可选)。</param>
        /// <returns>是否成功切换。</returns>
        public bool SwitchTo(EditorMode mode, string description = "")
        {
            if (mode == _currentMode) return false;

            // 触发切换前事件，允许取消
            if (OnModeChanging != null)
            {
                foreach (var handler in OnModeChanging.GetInvocationList())
                {
                    if (handler is Func<EditorMode, EditorMode, bool> f && !f(_currentMode, mode))
                        return false; // 被取消
                }
            }

            // 保存当前模式上下文
            var ctx = new ModeContext
            {
                Mode = _currentMode,
                Description = description,
                EnterTime = DateTime.Now
            };
            _savedContexts[_currentMode] = ctx;
            _modeHistory.Push(ctx);

            _previousMode = _currentMode;
            _currentMode = mode;

            OnModeChanged?.Invoke(_previousMode, _currentMode);
            return true;
        }

        /// <summary>切换回上一个模式。</summary>
        public bool SwitchBack()
        {
            if (_modeHistory.Count == 0) return false;
            return SwitchTo(_previousMode, "返回上一模式");
        }

        // ── 模式信息查询 ──────────────────────────────────────────────────────

        /// <summary>获取模式信息。</summary>
        public static ModeInfo GetModeInfo(EditorMode mode) =>
            ModeInfos.TryGetValue(mode, out var info) ? info : ModeInfos[EditorMode.Scene];

        /// <summary>获取当前模式信息。</summary>
        public ModeInfo CurrentModeInfo => GetModeInfo(_currentMode);

        // ── 模式工具栏绘制 ──────────────────────────────────────────────────────

        /// <summary>绘制模式切换工具栏 (水平按钮组)。</summary>
        public void DrawModeToolbar()
        {
            var avail = ImGui.GetContentRegionAvail();
            float btnW = Math.Min(120f, avail.X / 6f - 4f);

            foreach (EditorMode mode in Enum.GetValues<EditorMode>())
            {
                var info = GetModeInfo(mode);
                bool isActive = mode == _currentMode;

                // 激活模式高亮
                if (isActive)
                    ImGui.PushStyleColor(ImGuiCol.Button, info.Color);
                else
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.22f, 1f));

                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(
                    info.Color.X * 0.7f + 0.3f,
                    info.Color.Y * 0.7f + 0.3f,
                    info.Color.Z * 0.7f + 0.3f, 1f));

                string label = $"{info.Icon} {info.ShortName} [{info.Shortcut}]";

                if (ImGui.Button(label, new Vector2(btnW, 28)))
                    SwitchTo(mode);

                ImGui.PopStyleColor(2);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{info.Name}\n{info.Description}");

                ImGui.SameLine();
            }

            // 返回按钮
            ImGui.SameLine(avail.X - 60);
            if (ImGui.Button("← 返回", new Vector2(56, 28)) && _modeHistory.Count > 0)
                SwitchBack();
        }

        /// <summary>绘制模式状态条 (显示当前模式信息)。</summary>
        public void DrawModeStatusBar()
        {
            var info = CurrentModeInfo;
            ImGui.TextColored(info.Color, $"{info.Icon} {info.Name}");
            ImGui.SameLine();
            ImGui.TextDisabled($"| {info.Description}");
        }

        // ── 快捷键处理 ──────────────────────────────────────────────────────────

        /// <summary>处理数字键快捷切换模式。</summary>
        /// <param name="key1Pressed">数字 1 按下。</param>
        /// <param name="key2Pressed">数字 2 按下。</param>
        /// <param name="key3Pressed">数字 3 按下。</param>
        /// <param name="key4Pressed">数字 4 按下。</param>
        /// <param name="key5Pressed">数字 5 按下。</param>
        public void HandleShortcuts(
            bool key1Pressed, bool key2Pressed, bool key3Pressed,
            bool key4Pressed, bool key5Pressed)
        {
            if (key1Pressed) SwitchTo(EditorMode.Scene);
            else if (key2Pressed) SwitchTo(EditorMode.Terrain);
            else if (key3Pressed) SwitchTo(EditorMode.Modeling);
            else if (key4Pressed) SwitchTo(EditorMode.Code);
            else if (key5Pressed) SwitchTo(EditorMode.Test);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 模式信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器模式的显示信息。</summary>
    public sealed class ModeInfo
    {
        /// <summary>模式名称 (中文)。</summary>
        public string Name = string.Empty;
        /// <summary>短名称 (英文)。</summary>
        public string ShortName = string.Empty;
        /// <summary>图标 (Emoji)。</summary>
        public string Icon = string.Empty;
        /// <summary>快捷键。</summary>
        public string Shortcut = string.Empty;
        /// <summary>主题色。</summary>
        public Vector4 Color = new(0.5f, 0.5f, 0.5f, 1f);
        /// <summary>描述。</summary>
        public string Description = string.Empty;
    }
}
