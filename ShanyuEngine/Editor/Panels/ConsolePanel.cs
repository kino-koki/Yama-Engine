using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor;

using LogLevel = ShanyuEngine.Config.LogLevel;
namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 编辑器控制台面板 (ConsolePanel)
    //
    // 实现 IEditorConsole, 提供日志收集与展示:
    //   * 日志级别过滤 (Info / Warning / Error / Fatal / Debug)
    //   * 搜索框
    //   * 自动滚动到底部
    //   * 命令输入行 (cvar 设置 / 已注册命令)
    //   * 清除按钮
    //   * 双击日志复制到剪贴板
    //   * 时间戳 / 来源 / 级别显示
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ConsolePanel : EditorPanel, IEditorConsole
    {
        private const int MaxEntries = 4096;
        private readonly List<ConsoleLogEntry> _entries = new();
        private readonly object _lock = new();

        private bool _showInfo = true;
        private bool _showWarning = true;
        private bool _showError = true;
        private bool _showFatal = true;
        private bool _showDebug = false;
        private bool _autoScroll = true;
        private bool _scrollToBottom;

        private string _search = string.Empty;
        private string _command = string.Empty;
        private bool _commandFocus;

        // 级别 -> 颜色
        private static readonly Dictionary<LogLevel, Vector4> LevelColors = new()
        {
            { LogLevel.Info, new Vector4(0.86f, 0.86f, 0.88f, 1f) },
            { LogLevel.Warning, new Vector4(0.95f, 0.78f, 0.25f, 1f) },
            { LogLevel.Error, new Vector4(0.95f, 0.40f, 0.40f, 1f) },
            { LogLevel.Fatal, new Vector4(1.0f, 0.20f, 0.20f, 1f) },
            { LogLevel.Debug, new Vector4(0.40f, 0.85f, 0.95f, 1f) },
        };

        private static readonly string[] LevelTags =
        {
            "[INFO ]", "[WARN ]", "[ERROR]", "[FATAL]", "[DEBUG]",
        };

        public ConsolePanel() : base("控制台", "Console")
        {
            CanClose = true;
            MinSize = new Vector2(320, 160);
        }

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        // ─────────────────────── IEditorConsole ───────────────────────

        public void AddLog(LogLevel level, string message, string category = "", string source = "")
        {
            var entry = new ConsoleLogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message ?? string.Empty,
                Category = category ?? string.Empty,
                Source = source ?? string.Empty,
            };
            lock (_lock)
            {
                _entries.Add(entry);
                if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
            _scrollToBottom = _autoScroll;
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        // ─────────────────────── 绘制 ───────────────────────

        protected override void OnDraw(EditorContext ctx)
        {
            DrawFilterBar(ctx);
            ImGui.Separator();

            // 日志区域
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 1));
            ImGui.BeginChild("##ConsoleLog", new Vector2(0, ImGui.GetContentRegionAvail().Y - 28), false, ImGuiWindowFlags.AlwaysVerticalScrollbar);

            // 便于交替底色, 使用 Stripe
            List<ConsoleLogEntry> snapshot;
            lock (_lock) snapshot = _entries.ToList();

            int i = 0;
            foreach (var e in snapshot)
            {
                if (!PassFilter(e)) { i++; continue; }

                ImGui.PushID(i);
                var col = LevelColors[e.Level];
                ImGui.PushStyleColor(ImGuiCol.Text, col);
                ImGui.TextUnformatted($"{e.Time:HH:mm:ss} {LevelTags[(int)e.Level]}");
                ImGui.PopStyleColor();

                if (!string.IsNullOrEmpty(e.Source))
                {
                    ImGui.SameLine(0, 6);
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.7f, 1f, 1f));
                    ImGui.TextUnformatted($"[{e.Source}]");
                    ImGui.PopStyleColor();
                }

                ImGui.SameLine(0, 6);
                ImGui.TextWrapped(e.Message);

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    ImGui.SetClipboardText(e.Message);
                }

                ImGui.PopID();
                i++;
            }

            if (_scrollToBottom && ImGui.GetScrollMaxY() > 0)
            {
                ImGui.SetScrollHereY(1.0f);
                _scrollToBottom = false;
            }

            ImGui.EndChild();
            ImGui.PopStyleVar();

            ImGui.Separator();
            DrawCommandLine(ctx);
        }

        private void DrawFilterBar(EditorContext ctx)
        {
            CheckboxColored("信息", ref _showInfo, new Vector4(0.86f, 0.86f, 0.88f, 1f)); ImGui.SameLine();
            CheckboxColored("警告", ref _showWarning, new Vector4(0.95f, 0.78f, 0.25f, 1f)); ImGui.SameLine();
            CheckboxColored("错误", ref _showError, new Vector4(0.95f, 0.40f, 0.40f, 1f)); ImGui.SameLine();
            CheckboxColored("致命", ref _showFatal, new Vector4(1.0f, 0.20f, 0.20f, 1f)); ImGui.SameLine();
            CheckboxColored("调试", ref _showDebug, new Vector4(0.40f, 0.85f, 0.95f, 1f));

            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 180);
            ImGui.InputTextWithHint("##ConsoleSearch", "搜索日志...", ref _search, 128);
            ImGui.SameLine();
            ImGui.Checkbox("自动滚动", ref _autoScroll);
            ImGui.SameLine();
            if (ImGui.Button("清除")) Clear();
            ImGui.SameLine();
            if (ImGui.Button("复制全部")) CopyAll();
        }

        private static void CheckboxColored(string label, ref bool value, Vector4 color)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.Checkbox(label, ref value);
            ImGui.PopStyleColor();
        }

        private void DrawCommandLine(EditorContext ctx)
        {
            ImGui.TextDisabled(">");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (_commandFocus) { ImGui.SetKeyboardFocusHere(); _commandFocus = false; }
            bool enter = ImGui.InputTextWithHint("##Cmd", "输入命令或 cvar (例如: help / r.vsync 1)", ref _command, 256,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.Button("执行") || enter)
            {
                if (!string.IsNullOrWhiteSpace(_command))
                {
                    ctx.Console?.AddLog(LogLevel.Debug, "> " + _command, "cmd");
                    var commands = ctx.Editor?.Commands;
                    if (commands != null) commands.Execute(_command, ctx);
                    else ctx.Console?.AddLog(LogLevel.Warning, "命令系统未初始化", "cmd");
                }
                _command = string.Empty;
                _commandFocus = true;
            }
        }

        private void CopyAll()
        {
            lock (_lock)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var e in _entries)
                    sb.AppendLine($"{e.Time:HH:mm:ss} {LevelTags[(int)e.Level]} [{e.Source}] {e.Message}");
                ImGui.SetClipboardText(sb.ToString());
            }
        }

        private bool PassFilter(ConsoleLogEntry e)
        {
            switch (e.Level)
            {
                case LogLevel.Info: if (!_showInfo) return false; break;
                case LogLevel.Warning: if (!_showWarning) return false; break;
                case LogLevel.Error: if (!_showError) return false; break;
                case LogLevel.Fatal: if (!_showFatal) return false; break;
                case LogLevel.Debug: if (!_showDebug) return false; break;
            }
            if (!string.IsNullOrEmpty(_search) &&
                !e.Message.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !e.Source.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !e.Category.Contains(_search, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }
}
