// ==================================================================================================
//  CodeEditorPanel.cs — 山雨引擎内置代码编辑器面板
//
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ImGuiNET, ShanyuEngine.CodeEditing
//
//  功能:
//    1. 多文档标签页管理 (新建/打开/关闭/保存)
//    2. 语法高亮覆盖 (C#/HLSL, 基于 SyntaxHighlighter + ImDrawList)
//    3. 行号显示 (ImDrawList 绘制)
//    4. IntelliSense 补全弹窗 (Ctrl+Space 触发)
//    5. 签名提示 (输入 '(' 触发)
//    6. 搜索替换栏 (Ctrl+F / Ctrl+H)
//    7. 编译反馈面板 (CompileFeedbackAnalyzer)
//    8. 脚本模板创建 (ActorComponent / Pawn / GameMode)
//    9. 代码格式化 (CSharpFormatter)
//   10. 状态栏 (文件名 / 行:列 / 语言 / 修改标记)
//
//  架构说明:
//    面板继承 EditorPanel, 通过 OnDraw 渲染 ImGui 内容。
//    文本编辑使用 InputTextMultiline, 语法高亮通过 ImDrawList 在文本上方覆盖彩色 token。
//    编辑区域置于可滚动 ChildWindow 中, InputTextMultiline 高度设为全文高度,
//    使得滚动由 ChildWindow 管理, 语法高亮覆盖层可读取 ImGui.GetScrollY() 同步偏移。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.CodeEditing;

namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════════
    //  CodeEditorPanel
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 引擎内置代码编辑器面板。提供多文档编辑、语法高亮、智能补全、
    /// 编译反馈、搜索替换等完整代码编辑能力。
    /// </summary>
    public sealed class CodeEditorPanel : EditorPanel
    {
        // ── 标签页数据 ──────────────────────────────────────────────────────────

        /// <summary>单个打开的代码文档标签页状态。</summary>
        private sealed class EditorTab
        {
            public string FilePath = string.Empty;
            public string Content = string.Empty;
            public string SavedContent = string.Empty;
            public CodeLanguage Language = CodeLanguage.CSharp;
            public bool ScrollPending;
            public string Title => string.IsNullOrEmpty(FilePath)
                ? "Untitled"
                : Path.GetFileName(FilePath);

            public bool Modified => Content != SavedContent;
        }

        // ── 标签页 ──────────────────────────────────────────────────────────────

        private readonly List<EditorTab> _tabs = new();
        private int _activeTab = -1;
        private string _scriptsDir = "Scripts";

        // ── 文本缓冲 ────────────────────────────────────────────────────────────

        private string _buffer = string.Empty;
        private bool _bufferDirty;

        // ── 语法高亮缓存 ────────────────────────────────────────────────────────

        private List<(string Text, int Line, int Col, uint Color)> _cachedTokens = new();
        private string _cachedText = string.Empty;
        private CodeLanguage _cachedLang;

        // ── IntelliSense ──────────────────────────────────────────────────────────

        private readonly IntelliSenseProvider _intelliSense = new();
        private List<CompletionItem> _completionItems = new();
        private int _completionSel;
        private bool _completionOpen;
        private SignatureHelp? _signatureHelp;
        private bool _signatureVisible;
        private string _completionPrefix = string.Empty;

        // ── 搜索替换 ────────────────────────────────────────────────────────────

        private bool _showFind;
        private string _findText = string.Empty;
        private string _replaceText = string.Empty;
        private bool _matchCase;
        private bool _wholeWord;
        private int _findIdx = -1;
        private List<(int Line, int Col, int Len)> _findResults = new();

        // ── 编译 ────────────────────────────────────────────────────────────────

        private bool _showDiagnostics = true;
        private CompileResult? _compileResult;
        private string _compileStatus = string.Empty;

        // ── 新建文件对话框 ────────────────────────────────────────────────────────

        private bool _showNewDlg;
        private string _newName = "NewScript";
        private int _templateIdx;
        private static readonly string[] TemplateNames = { "ActorComponent", "Pawn", "GameMode" };

        // ── 打开文件对话框 ────────────────────────────────────────────────────────

        private bool _showOpenDlg;
        private string _openPath = string.Empty;
        private List<string> _dirFiles = new();
        private string _currentDir = string.Empty;

        // ── UI 状态 ──────────────────────────────────────────────────────────────

        private bool _showLineNumbers = true;
        private float _lineNumberWidth = 48f;
        private int _estimatedCursorLine = 1;
        private int _estimatedCursorCol = 1;
        private double _cursorBlinkTime;
        private bool _cursorBlinkOn = true;

        // ── 颜色 (VS Code Dark+ 风格) ───────────────────────────────────────────────

        private static readonly uint ColBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.117f, 0.117f, 0.117f, 1f));
        private static readonly uint ColBgAlt = ImGui.ColorConvertFloat4ToU32(new Vector4(0.094f, 0.094f, 0.094f, 1f));
        private static readonly uint ColLineNumber = ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.45f, 0.45f, 1f));
        private static readonly uint ColLineNumberActive = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.85f, 1f));
        private static readonly uint ColSelection = ImGui.ColorConvertFloat4ToU32(new Vector4(0.17f, 0.30f, 0.55f, 0.6f));
        private static readonly uint ColCursor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 1f));
        private static readonly uint ColFindMatch = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.40f, 0.10f, 0.7f));
        private static readonly uint ColFindCurrent = ImGui.ColorConvertFloat4ToU32(new Vector4(0.80f, 0.60f, 0.10f, 0.9f));
        private static readonly uint ColErrorSquiggle = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.3f, 0.3f, 0.7f));
        private static readonly uint ColErrorBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.12f, 0.12f, 0.4f));
        private static readonly uint ColWarningBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.28f, 0.10f, 0.4f));

        private static readonly Dictionary<TokenType, uint> TokenColors = new()
        {
            [TokenType.Keyword] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.34f, 0.61f, 0.84f, 1f)),
            [TokenType.Type] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.31f, 0.79f, 0.69f, 1f)),
            [TokenType.String] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.84f, 0.62f, 0.52f, 1f)),
            [TokenType.Comment] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.42f, 0.60f, 0.33f, 1f)),
            [TokenType.Number] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.71f, 0.51f, 1f, 1f)),
            [TokenType.Operator] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.71f, 0.71f, 0.71f, 1f)),
            [TokenType.MethodName] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.86f, 0.86f, 0.51f, 1f)),
            [TokenType.Identifier] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.83f, 0.83f, 0.85f, 1f)),
            [TokenType.Punctuation] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.71f, 0.71f, 0.71f, 1f)),
            [TokenType.Preprocessor] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.61f, 0.61f, 0.61f, 1f)),
            [TokenType.Whitespace] = 0,
            [TokenType.Unknown] = ImGui.ColorConvertFloat4ToU32(new Vector4(0.83f, 0.83f, 0.85f, 1f)),
        };

        // ══════════════════════════ 构造 ══════════════════════════

        public CodeEditorPanel() : base("代码编辑器", "CodeEditor")
        {
            CanClose = true;
            MinSize = new Vector2(420, 280);

            // 创建默认空文档
            AddTab("Untitled.cs", string.Empty, CodeLanguage.CSharp);
        }

        // ══════════════════════════ 主绘制 ══════════════════════════

        protected override void OnDraw(EditorContext ctx)
        {
            // 更新光标闪烁
            _cursorBlinkTime += ImGui.GetIO().DeltaTime;
            if (_cursorBlinkTime > 0.5) { _cursorBlinkOn = !_cursorBlinkOn; _cursorBlinkTime = 0; }

            // 检测全局快捷键
            HandleGlobalShortcuts();

            DrawToolbar(ctx);
            DrawTabBar();

            if (_showFind) DrawFindReplaceBar();

            DrawEditorArea(ctx);

            if (_showDiagnostics) DrawDiagnosticsPanel();

            DrawStatusBar();

            if (_completionOpen) DrawCompletionPopup();
            if (_signatureVisible) DrawSignatureHelp();

            if (_showNewDlg) DrawNewFileDialog();
            if (_showOpenDlg) DrawOpenFileDialog();
        }

        // ══════════════════════════ 工具栏 ══════════════════════════

        private void DrawToolbar(EditorContext ctx)
        {
            var avail = ImGui.GetContentRegionAvail();

            // ── 文件操作按钮组 ──
            if (ImGui.Button("新建##code_new"))
            {
                _showNewDlg = true;
                _newName = "NewScript";
                _templateIdx = 0;
            }

            ImGui.SameLine();
            if (ImGui.Button("打开##code_open"))
            {
                _showOpenDlg = true;
                _currentDir = Directory.Exists(_scriptsDir) ? _scriptsDir : Directory.GetCurrentDirectory();
                RefreshDirFiles();
            }

            ImGui.SameLine();
            if (ImGui.Button("保存##code_save"))
                SaveCurrentTab();

            ImGui.SameLine();
            if (ImGui.Button("全部保存##code_saveall"))
                SaveAllTabs();

            ImGui.SameLine();
            ImGui.SeparatorEx(ImGuiSeparatorFlags.Vertical);
            ImGui.SameLine();

            // ── 编辑操作按钮组 ──
            if (ImGui.Button("格式化##code_format"))
                FormatCurrentTab();

            ImGui.SameLine();
            if (ImGui.Button("编译##code_compile"))
                CompileCurrentTab();

            ImGui.SameLine();
            ImGui.SeparatorEx(ImGuiSeparatorFlags.Vertical);
            ImGui.SameLine();

            // ── 视图切换 ──
            ImGui.Checkbox("行号##code_linenums", ref _showLineNumbers);

            ImGui.SameLine();
            bool showFind = _showFind;
            if (ImGui.Checkbox("搜索替换##code_find", ref showFind))
                _showFind = showFind;

            ImGui.SameLine();
            bool showDiag = _showDiagnostics;
            if (ImGui.Checkbox("诊断##code_diag", ref showDiag))
                _showDiagnostics = showDiag;

            // ── 脚本目录输入 ──
            ImGui.SameLine();
            ImGui.PushItemWidth(150);
            ImGui.InputText("##scripts_dir", ref _scriptsDir, 256);
            ImGui.PopItemWidth();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("脚本保存目录");

            ImGui.Separator();
        }

        // ══════════════════════════ 标签页栏 ══════════════════════════

        private void DrawTabBar()
        {
            if (_tabs.Count == 0)
            {
                AddTab("Untitled.cs", string.Empty, CodeLanguage.CSharp);
            }

            if (ImGui.BeginTabBar("##code_tabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
            {
                for (int i = 0; i < _tabs.Count; i++)
                {
                    var tab = _tabs[i];
                    bool open = true;
                    string label = tab.Title + (tab.Modified ? " *" : "") + "##tab" + i;

                    if (ImGui.BeginTabItem(label, ref open))
                    {
                        if (_activeTab != i)
                        {
                            _activeTab = i;
                            _buffer = tab.Content;
                            _bufferDirty = true;
                        }
                        ImGui.EndTabItem();
                    }

                    if (!open)
                    {
                        CloseTab(i);
                        if (i > 0) i--;
                    }
                }
                ImGui.EndTabBar();
            }
        }

        // ══════════════════════════ 搜索替换栏 ══════════════════════════

        private void DrawFindReplaceBar()
        {
            var avail = ImGui.GetContentRegionAvail();

            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColBgAlt);
            ImGui.BeginChild("##find_bar", new Vector2(avail.X, 52), true);

            // 搜索行
            ImGui.Text("查找:");
            ImGui.SameLine();
            ImGui.PushItemWidth(200);
            if (ImGui.InputText("##find_input", ref _findText, 256, ImGuiInputTextFlags.EnterReturnsTrue))
                ExecuteFind();
            ImGui.PopItemWidth();

            ImGui.SameLine();
            if (ImGui.Button("上一个##find_prev")) FindPrev();
            ImGui.SameLine();
            if (ImGui.Button("下一个##find_next")) FindNext();
            ImGui.SameLine();
            ImGui.Checkbox("区分大小写##find_case", ref _matchCase);
            ImGui.SameLine();
            ImGui.Checkbox("全词匹配##find_word", ref _wholeWord);

            ImGui.SameLine();
            if (_findResults.Count > 0)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f),
                    $"  {_findIdx + 1}/{_findResults.Count}");
            else if (!string.IsNullOrEmpty(_findText))
                ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "  无匹配");

            // 替换行
            ImGui.Text("替换:");
            ImGui.SameLine();
            ImGui.PushItemWidth(200);
            ImGui.InputText("##replace_input", ref _replaceText, 256);
            ImGui.PopItemWidth();

            ImGui.SameLine();
            if (ImGui.Button("替换##replace_one")) ReplaceOne();
            ImGui.SameLine();
            if (ImGui.Button("全部替换##replace_all")) ReplaceAll();

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ══════════════════════════ 编辑器区域 ══════════════════════════

        private void DrawEditorArea(EditorContext ctx)
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;

            var tab = _tabs[_activeTab];
            var avail = ImGui.GetContentRegionAvail();

            // 预留诊断面板和状态栏的空间
            float diagHeight = _showDiagnostics ? 120 : 0;
            float statusHeight = 22;
            float editorHeight = avail.Y - diagHeight - statusHeight;
            if (editorHeight < 60) editorHeight = 60;

            float editorWidth = avail.X;
            float lineNumW = _showLineNumbers ? _lineNumberWidth : 0;
            float textWidth = editorWidth - lineNumW;

            // ── 更新语法高亮缓存 ──
            if (_bufferDirty || _cachedText != tab.Content || _cachedLang != tab.Language)
            {
                UpdateTokenCache(tab.Content, tab.Language);
                _bufferDirty = false;
            }

            // ── 计算文本总高度 ──
            int lineCount = tab.Content.Count(c => c == '\n') + 1;
            float lineHeight = ImGui.GetTextLineHeight();
            float lineSpacing = ImGui.GetTextLineHeightWithSpacing() - lineHeight;
            float totalTextHeight = lineCount * (lineHeight + lineSpacing);
            float framePad = ImGui.GetStyle().FramePadding.Y;

            // ── 行号子窗口 ──
            if (_showLineNumbers)
            {
                ImGui.PushStyleColor(ImGuiCol.ChildBg, ColBgAlt);
                ImGui.BeginChild("##line_numbers", new Vector2(lineNumW, editorHeight), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                DrawLineNumbers(tab, lineHeight, lineSpacing, framePad);
                ImGui.EndChild();
                ImGui.PopStyleColor();

                ImGui.SameLine(0, 0);
            }

            // ── 编辑器子窗口 (带滚动) ──
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColBg);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, ColBg);
            ImGui.BeginChild("##editor_scroll", new Vector2(textWidth, editorHeight), false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);

            // InputTextMultiline 高度设为全文高度, 让 ChildWindow 管理滚动
            float inputHeight = Math.Max(totalTextHeight + framePad * 2 + 4, editorHeight);

            // 设置 InputTextMultiline 文字颜色为透明, 用 ImDrawList 覆盖彩色文本
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, ColBg);
            ImGui.PushFont(ImGui.GetFont());

            var flags = ImGuiInputTextFlags.AllowTabInput
                     | ImGuiInputTextFlags.NoHorizontalScroll
                     | ImGuiInputTextFlags.CtrlEnterForNewLine;

            // 动态调整缓冲区大小
            uint bufSize = (uint)Math.Max(tab.Content.Length * 2 + 4096, 65536);

            bool changed = ImGui.InputTextMultiline("##code_input", ref _buffer, bufSize,
                new Vector2(textWidth - 14, inputHeight), flags);

            ImGui.PopFont();
            ImGui.PopStyleColor(2);

            // ── 文本变化处理 ──
            if (changed)
            {
                // 检测光标位置变化 (通过 diff)
                int changePos = FindFirstDifference(tab.Content, _buffer);
                UpdateCursorPosition(_buffer, changePos);

                tab.Content = _buffer;
                _cachedText = string.Empty; // 使缓存失效
                _bufferDirty = true;

                // 检测是否需要触发补全
                CheckCompletionTrigger(_buffer, changePos);

                // 检测是否需要触发签名提示
                CheckSignatureTrigger(_buffer, changePos);

                // 滚动到光标
                ScrollToCursor(lineHeight + lineSpacing, editorHeight);
            }

            // ── 语法高亮覆盖 ──
            DrawSyntaxHighlightOverlay(tab, lineHeight, lineSpacing, framePad);

            // ── 搜索结果高亮 ──
            DrawFindResultsOverlay(lineHeight, lineSpacing, framePad);

            // ── 错误波浪线 ──
            DrawErrorSquiggles(tab, lineHeight, lineSpacing, framePad);

            ImGui.EndChild();
            ImGui.PopStyleColor(2);
        }

        // ─────────────────────────── 行号 ───────────────────────────

        private void DrawLineNumbers(EditorTab tab, float lineHeight, float lineSpacing, float framePad)
        {
            var scrollY = ImGui.GetScrollY();
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();

            int lineCount = tab.Content.Count(c => c == '\n') + 1;
            int firstVisible = (int)(scrollY / (lineHeight + lineSpacing));
            int lastVisible = (int)((scrollY + size.Y) / (lineHeight + lineSpacing)) + 1;
            if (firstVisible < 0) firstVisible = 0;
            if (lastVisible >= lineCount) lastVisible = lineCount - 1;

            float x = min.X + _lineNumberWidth - 12;
            var font = ImGui.GetFont();
            float fontSize = ImGui.GetFontSize();

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                float y = min.Y + framePad + i * (lineHeight + lineSpacing) - scrollY;
                if (y < min.Y - lineHeight || y > min.Y + size.Y) continue;

                bool isActive = (i + 1) == _estimatedCursorLine;
                uint col = isActive ? ColLineNumberActive : ColLineNumber;
                string num = (i + 1).ToString();
                drawList.AddText(font, fontSize, new Vector2(x, y), col, num);
            }

            // 分隔线
            drawList.AddLine(
                new Vector2(min.X + _lineNumberWidth - 1, min.Y),
                new Vector2(min.X + _lineNumberWidth - 1, min.Y + size.Y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.23f, 0.5f)));
        }

        // ─────────────────────────── 语法高亮覆盖 ───────────────────────────

        private void DrawSyntaxHighlightOverlay(EditorTab tab, float lineHeight, float lineSpacing, float framePad)
        {
            if (_cachedTokens.Count == 0) return;

            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var scrollY = ImGui.GetScrollY();
            var font = ImGui.GetFont();
            float fontSize = ImGui.GetFontSize();
            float charWidth = font.GetCharAdvance('M');
            float startX = min.X + ImGui.GetStyle().FramePadding.X;
            float startY = min.Y + framePad - scrollY;

            int firstVisibleLine = Math.Max(0, (int)(scrollY / (lineHeight + lineSpacing)) - 1);
            int lastVisibleLine = (int)((scrollY + size.Y) / (lineHeight + lineSpacing)) + 1;

            foreach (var token in _cachedTokens)
            {
                if (token.Color == 0) continue; // 跳过空白
                if (token.Line < firstVisibleLine || token.Line > lastVisibleLine) continue;

                float x = startX + token.Col * charWidth;
                float y = startY + token.Line * (lineHeight + lineSpacing);

                if (y < min.Y - lineHeight || y > min.Y + size.Y) continue;

                drawList.AddText(font, fontSize, new Vector2(x, y), token.Color, token.Text);
            }
        }

        // ─────────────────────────── 搜索结果高亮 ───────────────────────────

        private void DrawFindResultsOverlay(float lineHeight, float lineSpacing, float framePad)
        {
            if (_findResults.Count == 0 || string.IsNullOrEmpty(_findText)) return;

            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var scrollY = ImGui.GetScrollY();
            var font = ImGui.GetFont();
            float charWidth = font.GetCharAdvance('M');
            float startX = min.X + ImGui.GetStyle().FramePadding.X;
            float startY = min.Y + framePad - scrollY;

            for (int i = 0; i < _findResults.Count; i++)
            {
                var (line, col, len) = _findResults[i];
                float x = startX + col * charWidth;
                float y = startY + line * (lineHeight + lineSpacing);

                if (y < min.Y - lineHeight || y > min.Y + lineHeight * 2) continue;

                uint col2 = (i == _findIdx) ? ColFindCurrent : ColFindMatch;
                float w = len * charWidth;
                drawList.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + lineHeight), col2);
            }
        }

        // ─────────────────────────── 错误波浪线 ───────────────────────────

        private void DrawErrorSquiggles(EditorTab tab, float lineHeight, float lineSpacing, float framePad)
        {
            if (_compileResult == null || _compileResult.Messages.Count == 0) return;

            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            var scrollY = ImGui.GetScrollY();
            var font = ImGui.GetFont();
            float charWidth = font.GetCharAdvance('M');
            float startX = min.X + ImGui.GetStyle().FramePadding.X;
            float startY = min.Y + framePad - scrollY;

            foreach (var msg in _compileResult.Messages)
            {
                if (msg.Severity != CodeDiagnosticSeverity.Error) continue;
                int line = msg.Line > 0 ? msg.Line - 1 : 0;
                int col = msg.Column > 0 ? msg.Column - 1 : 0;

                // 背景高亮
                float y = startY + line * (lineHeight + lineSpacing);
                if (y < min.Y - lineHeight || y > min.Y + lineHeight * 2) continue;
                drawList.AddRectFilled(
                    new Vector2(startX, y),
                    new Vector2(startX + 1000, y + lineHeight),
                    ColErrorBg);

                // 波浪线
                float squiggleY = y + lineHeight - 2;
                for (int sx = 0; sx < 10; sx++)
                {
                    float x1 = startX + col * charWidth + sx * charWidth;
                    float x2 = x1 + charWidth * 0.5f;
                    float x3 = x1 + charWidth;
                    drawList.AddLine(new Vector2(x1, squiggleY), new Vector2(x2, squiggleY + 2), ColErrorSquiggle, 1f);
                    drawList.AddLine(new Vector2(x2, squiggleY + 2), new Vector2(x3, squiggleY), ColErrorSquiggle, 1f);
                }
            }
        }

        // ══════════════════════════ 诊断面板 ══════════════════════════

        private void DrawDiagnosticsPanel()
        {
            var avail = ImGui.GetContentRegionAvail();
            float statusHeight = 22;
            float diagHeight = avail.Y - statusHeight;
            if (diagHeight < 60) diagHeight = 60;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColBgAlt);
            ImGui.BeginChild("##diagnostics", new Vector2(avail.X, diagHeight), true);

            // 标题栏
            ImGui.Text("编译输出");
            ImGui.SameLine();

            if (_compileResult != null)
            {
                var (errCol, warnCol) = _compileResult.ErrorCount > 0
                    ? (new Vector4(0.9f, 0.3f, 0.3f, 1f), new Vector4(0.95f, 0.75f, 0.2f, 1f))
                    : (new Vector4(0.3f, 0.85f, 0.4f, 1f), new Vector4(0.95f, 0.75f, 0.2f, 1f));

                ImGui.SameLine();
                ImGui.TextColored(errCol, $"  错误: {_compileResult.ErrorCount}");
                ImGui.SameLine();
                ImGui.TextColored(warnCol, $"  警告: {_compileResult.WarningCount}");
                ImGui.SameLine();
                if (_compileResult.Success)
                    ImGui.TextColored(new Vector4(0.3f, 0.85f, 0.4f, 1f), "  编译成功");
                else
                    ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "  编译失败");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.6f, 1f),
                    $"  耗时: {_compileResult.CompileTime.TotalMilliseconds:F0}ms");
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.6f, 1f), "  (尚未编译)");
            }

            ImGui.Separator();

            // 消息列表
            if (_compileResult != null && _compileResult.Messages.Count > 0)
            {
                ImGui.BeginChild("##diag_list", new Vector2(-1, -1), false);

                for (int i = 0; i < _compileResult.Messages.Count; i++)
                {
                    var msg = _compileResult.Messages[i];
                    var color = msg.Severity switch
                    {
                        CodeDiagnosticSeverity.Error => new Vector4(0.9f, 0.3f, 0.3f, 1f),
                        CodeDiagnosticSeverity.Warning => new Vector4(0.95f, 0.75f, 0.2f, 1f),
                        _ => new Vector4(0.55f, 0.55f, 0.6f, 1f),
                    };

                    string severity = msg.Severity switch
                    {
                        CodeDiagnosticSeverity.Error => "error",
                        CodeDiagnosticSeverity.Warning => "warning",
                        _ => "info",
                    };

                    string text = $"  [{msg.Code}] {severity} {msg.FilePath}:{msg.Line}:{msg.Column} - {msg.Message}";

                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    ImGui.TextUnformatted(text);
                    ImGui.PopStyleColor();

                    // 点击跳转
                    if (ImGui.IsItemClicked())
                    {
                        // 尝试跳转到该文件和行
                        var fileName = Path.GetFileName(msg.FilePath);
                        for (int t = 0; t < _tabs.Count; t++)
                        {
                            if (Path.GetFileName(_tabs[t].FilePath) == fileName)
                            {
                                _activeTab = t;
                                _buffer = _tabs[t].Content;
                                _bufferDirty = true;
                                _estimatedCursorLine = msg.Line;
                                break;
                            }
                        }
                    }
                }

                ImGui.EndChild();
            }
            else
            {
                ImGui.TextDisabled("  点击工具栏 \"编译\" 按钮运行编译分析");
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ══════════════════════════ 状态栏 ══════════════════════════

        private void DrawStatusBar()
        {
            var avail = ImGui.GetContentRegionAvail();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColBgAlt);
            ImGui.BeginChild("##status_bar", new Vector2(avail.X, 20), false);

            var tab = _activeTab >= 0 && _activeTab < _tabs.Count ? _tabs[_activeTab] : null;
            if (tab != null)
            {
                // 文件名 + 修改标记
                string modFlag = tab.Modified ? " ●" : "";
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.75f, 1f), $"  {tab.Title}{modFlag}");

                ImGui.SameLine(200);
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.6f, 1f),
                    $"行 {_estimatedCursorLine}, 列 {_estimatedCursorCol}");

                ImGui.SameLine(340);
                string langStr = tab.Language switch
                {
                    CodeLanguage.CSharp => "C#",
                    CodeLanguage.Hlsl => "HLSL",
                    _ => "纯文本",
                };
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.6f, 1f), langStr);

                // 行数
                int totalLines = tab.Content.Count(c => c == '\n') + 1;
                int totalChars = tab.Content.Length;
                ImGui.SameLine(400);
                ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.5f, 1f),
                    $"{totalLines} 行, {totalChars} 字符");

                // 补全状态
                if (_completionOpen && _completionItems.Count > 0)
                {
                    ImGui.SameLine(avail.X - 120);
                    ImGui.TextColored(new Vector4(0.3f, 0.6f, 0.95f, 1f),
                        $"补全: {_completionItems.Count} 项");
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ══════════════════════════ 补全弹窗 ══════════════════════════

        private void DrawCompletionPopup()
        {
            if (_completionItems.Count == 0)
            {
                _completionOpen = false;
                return;
            }

            var viewport = ImGui.GetMainViewport();
            var pos = ImGui.GetCursorScreenPos();
            pos.Y += ImGui.GetTextLineHeightWithSpacing();

            // 确保弹窗在屏幕内
            float popupW = 300;
            float popupH = Math.Min(_completionItems.Count * 20f + 8, 200);
            if (pos.X + popupW > viewport.Size.X) pos.X = viewport.Size.X - popupW;
            if (pos.Y + popupH > viewport.Size.Y) pos.Y -= popupH + ImGui.GetTextLineHeightWithSpacing() * 2;

            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(new Vector2(popupW, popupH));
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 0.98f)));
            ImGui.PushStyleColor(ImGuiCol.Border, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, 0.8f)));

            if (ImGui.Begin("##completion_popup", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
            {
                // 筛选
                var filtered = _completionItems
                    .Where(i => string.IsNullOrEmpty(_completionPrefix) ||
                        i.DisplayText.StartsWith(_completionPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filtered.Count == 0)
                {
                    ImGui.TextDisabled("  无匹配项");
                }
                else
                {
                    if (_completionSel >= filtered.Count) _completionSel = 0;

                    // 绘制补全项列表
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        var item = filtered[i];
                        bool selected = i == _completionSel;

                        // 图标颜色
                        var iconColor = item.IconType switch
                        {
                            CompletionIconType.Class => new Vector4(0.31f, 0.79f, 0.69f, 1f),
                            CompletionIconType.Method => new Vector4(0.86f, 0.86f, 0.51f, 1f),
                            CompletionIconType.Property => new Vector4(0.71f, 0.51f, 1f, 1f),
                            CompletionIconType.Field => new Vector4(0.71f, 0.71f, 0.71f, 1f),
                            CompletionIconType.Keyword => new Vector4(0.34f, 0.61f, 0.84f, 1f),
                            CompletionIconType.Snippet => new Vector4(0.84f, 0.62f, 0.52f, 1f),
                            CompletionIconType.Variable => new Vector4(0.55f, 0.82f, 0.55f, 1f),
                            _ => new Vector4(0.6f, 0.6f, 0.65f, 1f),
                        };

                        string icon = item.IconType switch
                        {
                            CompletionIconType.Class => "C",
                            CompletionIconType.Method => "M",
                            CompletionIconType.Property => "P",
                            CompletionIconType.Field => "F",
                            CompletionIconType.Keyword => "K",
                            CompletionIconType.Snippet => "S",
                            CompletionIconType.Variable => "V",
                            CompletionIconType.Enum => "E",
                            CompletionIconType.Struct => "T",
                            CompletionIconType.Interface => "I",
                            CompletionIconType.Namespace => "N",
                            _ => "?",
                        };

                        if (ImGui.Selectable($"##comp_{i}", selected, ImGuiSelectableFlags.None, new Vector2(-1, 18)))
                        {
                            _completionSel = i;
                            ApplyCompletion(filtered[i]);
                        }

                        // 悬停时自动选中
                        if (ImGui.IsItemHovered())
                            _completionSel = i;

                        // 绘制图标和文本
                        var p = ImGui.GetItemRectMin();
                        var dl = ImGui.GetWindowDrawList();
                        var font = ImGui.GetFont();
                        float fs = ImGui.GetFontSize();
                        dl.AddText(font, fs, new Vector2(p.X + 4, p.Y + 2),
                            ImGui.ColorConvertFloat4ToU32(iconColor), icon);
                        dl.AddText(font, fs, new Vector2(p.X + 24, p.Y + 2),
                            ImGui.ColorConvertFloat4ToU32(new Vector4(0.83f, 0.83f, 0.85f, 1f)),
                            item.DisplayText);

                        // 描述 (如果有)
                        if (!string.IsNullOrEmpty(item.Description))
                        {
                            var descSize = ImGui.CalcTextSize(item.DisplayText);
                            dl.AddText(font, fs, new Vector2(p.X + 28 + descSize.X, p.Y + 2),
                                ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.55f, 1f)),
                                "  " + item.Description);
                        }
                    }

                    // 键盘导航
                    if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                    {
                        _completionSel = Math.Min(_completionSel + 1, filtered.Count - 1);
                        _cursorBlinkOn = true; _cursorBlinkTime = 0;
                    }
                    if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                    {
                        _completionSel = Math.Max(_completionSel - 1, 0);
                        _cursorBlinkOn = true; _cursorBlinkTime = 0;
                    }
                    if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Tab))
                    {
                        ApplyCompletion(filtered[_completionSel]);
                    }
                    if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    {
                        _completionOpen = false;
                    }
                }

                ImGui.End();
            }
            ImGui.PopStyleColor(2);
        }

        // ══════════════════════════ 签名提示 ══════════════════════════

        private void DrawSignatureHelp()
        {
            if (_signatureHelp == null || !_signatureHelp.IsActive)
            {
                _signatureVisible = false;
                return;
            }

            var pos = ImGui.GetCursorScreenPos();
            pos.Y += ImGui.GetTextLineHeightWithSpacing();
            ImGui.SetNextWindowPos(pos);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 0.98f)));
            ImGui.PushStyleColor(ImGuiCol.Border, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.35f, 0.8f)));

            if (ImGui.Begin("##signature_help", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.AlwaysAutoResize))
            {
                for (int i = 0; i < _signatureHelp.Signatures.Count; i++)
                {
                    var sig = _signatureHelp.Signatures[i];
                    bool active = i == _signatureHelp.ActiveSignature;

                    if (active)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.86f, 0.86f, 0.51f, 1f));
                    else
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.65f, 1f));

                    // 高亮当前参数
                    string display = sig.DisplayString;
                    if (sig.Parameters.Count > 0 && _signatureHelp.ActiveParameter < sig.Parameters.Count)
                    {
                        var param = sig.Parameters[_signatureHelp.ActiveParameter];
                        // 在显示字符串中高亮当前参数 (简化: 直接在参数名后加标记)
                        ImGui.Text("  " + display);
                    }
                    else
                    {
                        ImGui.Text("  " + display);
                    }

                    ImGui.PopStyleColor();

                    if (!string.IsNullOrEmpty(sig.Documentation))
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.55f, 1f), "  " + sig.Documentation);
                    }
                }

                ImGui.End();
            }
            ImGui.PopStyleColor(2);
        }

        // ══════════════════════════ 新建文件对话框 ══════════════════════════

        private void DrawNewFileDialog()
        {
            ImGui.OpenPopup("新建脚本##new_script_popup");
            ImGui.SetNextWindowSize(new Vector2(400, 200));
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 1f)));

            if (ImGui.BeginPopupModal("新建脚本##new_script_popup", ref _showNewDlg,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
            {
                ImGui.Text("脚本名称:");
                ImGui.InputText("##new_name", ref _newName, 64);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("不含扩展名, 将自动添加 .cs");

                ImGui.Separator();
                ImGui.Text("模板:");

                for (int i = 0; i < TemplateNames.Length; i++)
                {
                    if (ImGui.RadioButton(TemplateNames[i] + "##template_" + i, ref _templateIdx, i))
                    { }
                }

                ImGui.Separator();

                // 模板预览
                ImGui.TextDisabled("预览:");
                ImGui.BeginChild("##template_preview", new Vector2(-1, 80), true);
                string preview = _templateIdx switch
                {
                    0 => "// ActorComponent 模板\nusing ShanyuGame;\n\npublic sealed class X : ShanyuScript\n{\n    public override void OnStart() { }\n    public override void OnUpdate(float dt) { }\n}",
                    1 => "// Pawn 模板\nusing ShanyuGame;\n\npublic sealed class X : ShanyuScript\n{\n    public override void OnUpdate(float dt)\n    {\n        Log(\"Pawn tick\");\n    }\n}",
                    _ => "// GameMode 模板\nusing ShanyuGame;\n\npublic sealed class X : ShanyuScript\n{\n    public override void OnStart()\n    {\n        Log(\"Game started\");\n    }\n}",
                };
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), preview);
                ImGui.EndChild();

                ImGui.Separator();

                if (ImGui.Button("创建##create_script"))
                {
                    CreateNewScript();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("取消##cancel_new"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
            ImGui.PopStyleColor();
        }

        // ══════════════════════════ 打开文件对话框 ══════════════════════════

        private void DrawOpenFileDialog()
        {
            ImGui.OpenPopup("打开文件##open_file_popup");
            ImGui.SetNextWindowSize(new Vector2(500, 400));
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 1f)));

            if (ImGui.BeginPopupModal("打开文件##open_file_popup", ref _showOpenDlg,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
            {
                // 目录导航
                ImGui.Text("目录:");
                ImGui.SameLine();
                ImGui.PushItemWidth(350);
                ImGui.InputText("##dir_path", ref _currentDir, 512);
                ImGui.PopItemWidth();
                ImGui.SameLine();
                if (ImGui.Button("刷新##refresh_dir"))
                    RefreshDirFiles();
                ImGui.SameLine();
                if (ImGui.Button("上级##parent_dir"))
                {
                    var parent = Directory.GetParent(_currentDir);
                    if (parent != null) { _currentDir = parent.FullName; RefreshDirFiles(); }
                }

                ImGui.Separator();

                // 文件列表
                ImGui.BeginChild("##file_list", new Vector2(-1, 280), true);

                // 子目录
                try
                {
                    foreach (var dir in Directory.GetDirectories(_currentDir))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith(".")) continue;
                        if (ImGui.Selectable($"[目录] {name}##dir_{name}"))
                        {
                            _currentDir = dir;
                            RefreshDirFiles();
                        }
                    }
                }
                catch { }

                // 文件
                foreach (var file in _dirFiles)
                {
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    bool isCode = ext is ".cs" or ".hlsl" or ".txt" or ".json" or ".xml";

                    var color = isCode
                        ? new Vector4(0.83f, 0.83f, 0.85f, 1f)
                        : new Vector4(0.5f, 0.5f, 0.55f, 1f);

                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    if (ImGui.Selectable($"  {name}##file_{name}"))
                    {
                        _openPath = file;
                    }
                    ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(file);

                    if (ImGui.IsItemClicked() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        OpenFile(file);
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndChild();

                ImGui.Separator();

                ImGui.Text("选中:");
                ImGui.SameLine();
                ImGui.PushItemWidth(350);
                ImGui.InputText("##open_path", ref _openPath, 512);
                ImGui.PopItemWidth();

                ImGui.SameLine();
                if (ImGui.Button("打开##do_open"))
                {
                    if (!string.IsNullOrEmpty(_openPath) && File.Exists(_openPath))
                    {
                        OpenFile(_openPath);
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("取消##cancel_open"))
                    ImGui.CloseCurrentPopup();

                ImGui.EndPopup();
            }
            ImGui.PopStyleColor();
        }

        // ══════════════════════════ 全局快捷键 ══════════════════════════

        private void HandleGlobalShortcuts()
        {
            var io = ImGui.GetIO();

            // Ctrl+S: 保存
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S))
            {
                SaveCurrentTab();
            }

            // Ctrl+Shift+S: 全部保存
            if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.S))
            {
                SaveAllTabs();
            }

            // Ctrl+F: 搜索
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F))
            {
                _showFind = true;
            }

            // Ctrl+H: 替换
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.H))
            {
                _showFind = true;
            }

            // Ctrl+Space: 补全
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Space))
            {
                RequestCompletion();
            }

            // Ctrl+Shift+F: 格式化
            if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.F))
            {
                FormatCurrentTab();
            }
        }

        // ══════════════════════════ 文件操作 ══════════════════════════

        private void AddTab(string filePath, string content, CodeLanguage language)
        {
            var tab = new EditorTab
            {
                FilePath = filePath,
                Content = content,
                SavedContent = content,
                Language = language,
            };
            _tabs.Add(tab);
            _activeTab = _tabs.Count - 1;
            _buffer = content;
            _bufferDirty = true;
            _cachedText = string.Empty;
        }

        private void CloseTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;

            var tab = _tabs[index];
            if (tab.Modified)
            {
                // 简化: 直接关闭 (未来可添加保存确认对话框)
            }

            _tabs.RemoveAt(index);
            if (_activeTab >= _tabs.Count)
                _activeTab = _tabs.Count - 1;

            if (_activeTab >= 0)
            {
                _buffer = _tabs[_activeTab].Content;
                _bufferDirty = true;
            }
        }

        private void SaveCurrentTab()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var tab = _tabs[_activeTab];

            if (string.IsNullOrEmpty(tab.FilePath) || tab.FilePath == "Untitled.cs")
            {
                // 使用脚本目录
                string dir = _scriptsDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                tab.FilePath = Path.Combine(dir, "Script_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".cs");
            }

            try
            {
                var dir = Path.GetDirectoryName(tab.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(tab.FilePath, tab.Content, System.Text.Encoding.UTF8);
                tab.SavedContent = tab.Content;
            }
            catch (Exception) { /* 忽略写入错误 */ }
        }

        private void SaveAllTabs()
        {
            foreach (var tab in _tabs)
            {
                if (string.IsNullOrEmpty(tab.FilePath)) continue;
                try
                {
                    File.WriteAllText(tab.FilePath, tab.Content, System.Text.Encoding.UTF8);
                    tab.SavedContent = tab.Content;
                }
                catch { }
            }
        }

        private void OpenFile(string filePath)
        {
            // 检查是否已打开
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].FilePath == filePath)
                {
                    _activeTab = i;
                    _buffer = _tabs[i].Content;
                    _bufferDirty = true;
                    return;
                }
            }

            try
            {
                var content = File.ReadAllText(filePath);
                var lang = CodeLanguage.Plain;
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".cs") lang = CodeLanguage.CSharp;
                else if (ext == ".hlsl") lang = CodeLanguage.Hlsl;

                AddTab(filePath, content, lang);
            }
            catch { }
        }

        private void CreateNewScript()
        {
            string name = string.IsNullOrWhiteSpace(_newName) ? "NewScript" : _newName;
            name = SanitizeIdentifier(name);
            string dir = _scriptsDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".cs");

            string content = _templateIdx switch
            {
                0 => $"// {name}.cs — ActorComponent 脚本\nusing ShanyuGame;\n\npublic sealed class {name} : ShanyuScript\n{{\n    public override void OnStart()\n    {{\n    }}\n\n    public override void OnUpdate(float deltaTime)\n    {{\n    }}\n}}\n",
                1 => $"// {name}.cs — Pawn 脚本\nusing ShanyuGame;\n\npublic sealed class {name} : ShanyuScript\n{{\n    public override void OnUpdate(float deltaTime)\n    {{\n        Log(\"{name} tick\");\n    }}\n}}\n",
                _ => $"// {name}.cs — GameMode 脚本\nusing ShanyuGame;\n\npublic sealed class {name} : ShanyuScript\n{{\n    public override void OnStart()\n    {{\n        Log(\"Game started\");\n    }}\n}}\n",
            };

            AddTab(path, content, CodeLanguage.CSharp);
            _showNewDlg = false;
        }

        // ══════════════════════════ 编辑操作 ══════════════════════════

        private void FormatCurrentTab()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var tab = _tabs[_activeTab];

            if (tab.Language != CodeLanguage.CSharp) return;

            string formatted = CSharpFormatter.Format(tab.Content);
            tab.Content = formatted;
            _buffer = formatted;
            _cachedText = string.Empty;
            _bufferDirty = true;
        }

        private void CompileCurrentTab()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var tab = _tabs[_activeTab];

            // 创建临时 CodeDocument 进行分析
            var doc = new CodeDocument(tab.FilePath, tab.Content)
            {
                Language = tab.Language
            };

            _compileResult = CompileFeedbackAnalyzer.Analyze(doc);
        }

        // ══════════════════════════ 智能补全 ══════════════════════════

        private void RequestCompletion()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var tab = _tabs[_activeTab];

            var doc = new CodeDocument(tab.FilePath, tab.Content) { Language = tab.Language };
            int pos = EstimateCursorOffset(tab.Content);
            var items = _intelliSense.GetCompletionItems(doc, pos);

            if (items.Count > 0)
            {
                _completionItems = items.ToList();
                _completionSel = 0;
                _completionOpen = true;
                _completionPrefix = ExtractPrefix(tab.Content, pos);
                _cursorBlinkOn = true; _cursorBlinkTime = 0;
            }
        }

        private void CheckCompletionTrigger(string text, int changePos)
        {
            if (changePos <= 0 || changePos > text.Length) return;
            char c = text[changePos - 1];
            // 输入 '.' 触发成员补全
            if (c == '.')
            {
                RequestCompletion();
            }
        }

        private void CheckSignatureTrigger(string text, int changePos)
        {
            if (changePos <= 0 || changePos > text.Length) return;
            char c = text[changePos - 1];
            if (c == '(')
            {
                if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
                var tab = _tabs[_activeTab];
                var doc = new CodeDocument(tab.FilePath, text) { Language = tab.Language };
                var help = _intelliSense.GetSignatureHelp(doc, changePos);
                if (help != null && help.IsActive)
                {
                    _signatureHelp = help;
                    _signatureVisible = true;
                }
            }
            else if (c == ')')
            {
                _signatureVisible = false;
            }
        }

        private void ApplyCompletion(CompletionItem item)
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var tab = _tabs[_activeTab];

            int pos = EstimateCursorOffset(tab.Content);
            if (pos < 0 || pos > tab.Content.Length) return;

            // 找到前缀开始位置
            int prefixStart = pos;
            while (prefixStart > 0 && (char.IsLetterOrDigit(tab.Content[prefixStart - 1]) || tab.Content[prefixStart - 1] == '_'))
                prefixStart--;

            // 替换前缀为补全文本
            string newText = tab.Content.Substring(0, prefixStart) + item.InsertText + tab.Content.Substring(pos);
            tab.Content = newText;
            _buffer = newText;
            _cachedText = string.Empty;
            _bufferDirty = true;

            _completionOpen = false;

            // 更新光标位置
            int newPos = prefixStart + item.InsertText.Length;
            UpdateCursorPosition(newText, newPos);
        }

        // ══════════════════════════ 搜索替换 ══════════════════════════

        private void ExecuteFind()
        {
            _findResults.Clear();
            _findIdx = -1;

            if (_activeTab < 0 || string.IsNullOrEmpty(_findText)) return;
            var tab = _tabs[_activeTab];
            var text = tab.Content;
            var comparison = _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            int idx = 0;
            while ((idx = text.IndexOf(_findText, idx, comparison)) >= 0)
            {
                var (line, col) = OffsetToLineCol(text, idx);
                _findResults.Add((line, col, _findText.Length));
                idx += _findText.Length;
            }

            if (_findResults.Count > 0)
                _findIdx = 0;
        }

        private void FindNext()
        {
            if (_findResults.Count == 0) return;
            _findIdx = (_findIdx + 1) % _findResults.Count;
            var (line, col, _) = _findResults[_findIdx];
            _estimatedCursorLine = line + 1;
            _estimatedCursorCol = col + 1;
        }

        private void FindPrev()
        {
            if (_findResults.Count == 0) return;
            _findIdx = (_findIdx - 1 + _findResults.Count) % _findResults.Count;
            var (line, col, _) = _findResults[_findIdx];
            _estimatedCursorLine = line + 1;
            _estimatedCursorCol = col + 1;
        }

        private void ReplaceOne()
        {
            if (_activeTab < 0 || _findIdx < 0 || _findIdx >= _findResults.Count) return;
            var tab = _tabs[_activeTab];
            var (line, col, len) = _findResults[_findIdx];

            int offset = LineColToOffset(tab.Content, line, col);
            string newText = tab.Content.Substring(0, offset) + _replaceText +
                              tab.Content.Substring(offset + len);

            tab.Content = newText;
            _buffer = newText;
            _cachedText = string.Empty;
            _bufferDirty = true;

            ExecuteFind();
        }

        private void ReplaceAll()
        {
            if (_activeTab < 0 || string.IsNullOrEmpty(_findText)) return;
            var tab = _tabs[_activeTab];

            var comparison = _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string newText = tab.Content.Replace(_findText, _replaceText, comparison);

            tab.Content = newText;
            _buffer = newText;
            _cachedText = string.Empty;
            _bufferDirty = true;

            ExecuteFind();
        }

        // ══════════════════════════ 辅助方法 ══════════════════════════

        private void UpdateTokenCache(string text, CodeLanguage lang)
        {
            _cachedTokens.Clear();
            if (string.IsNullOrEmpty(text)) return;

            var tokenizer = SyntaxTokenizer.Create(lang);
            var tokens = tokenizer.Tokenize(text);

            foreach (var t in tokens)
            {
                uint color = TokenColors.GetValueOrDefault(t.Type, 0);
                if (color == 0) continue;
                _cachedTokens.Add((t.Text, t.Line, t.Column, color));
            }

            _cachedText = text;
            _cachedLang = lang;
        }

        private void UpdateCursorPosition(string text, int changePos)
        {
            var (line, col) = OffsetToLineCol(text, Math.Clamp(changePos, 0, text.Length));
            _estimatedCursorLine = line + 1;
            _estimatedCursorCol = col + 1;
        }

        private void ScrollToCursor(float lineHeightWithSpacing, float viewportHeight)
        {
            float cursorY = (_estimatedCursorLine - 1) * lineHeightWithSpacing;
            float scrollY = ImGui.GetScrollY();
            float visibleStart = scrollY;
            float visibleEnd = scrollY + viewportHeight - lineHeightWithSpacing * 2;

            if (cursorY < visibleStart)
                ImGui.SetScrollY(Math.Max(0, cursorY - lineHeightWithSpacing));
            else if (cursorY > visibleEnd)
                ImGui.SetScrollY(cursorY - viewportHeight + lineHeightWithSpacing * 3);
        }

        private int EstimateCursorOffset(string text)
        {
            // 估算光标偏移: 基于行和列
            int offset = 0;
            int line = _estimatedCursorLine - 1;
            int col = _estimatedCursorCol - 1;

            for (int i = 0; i < line && offset < text.Length; i++)
            {
                while (offset < text.Length && text[offset] != '\n') offset++;
                if (offset < text.Length) offset++; // 跳过 \n
            }

            return Math.Min(offset + col, text.Length);
        }

        private static (int line, int col) OffsetToLineCol(string text, int offset)
        {
            if (offset <= 0) return (0, 0);
            int line = 0, col = 0;
            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; col = 0; }
                else col++;
            }
            return (line, col);
        }

        private static int LineColToOffset(string text, int line, int col)
        {
            int offset = 0;
            for (int i = 0; i < line && offset < text.Length; i++)
            {
                while (offset < text.Length && text[offset] != '\n') offset++;
                if (offset < text.Length) offset++;
            }
            return Math.Min(offset + col, text.Length);
        }

        private static int FindFirstDifference(string a, string b)
        {
            int minLen = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < minLen && a[i] == b[i]) i++;
            return i;
        }

        private static string ExtractPrefix(string text, int pos)
        {
            int start = pos;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
                start--;
            return text.Substring(start, pos - start);
        }

        private static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "NewScript";
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var result = new string(chars);
            return char.IsDigit(result[0]) ? "_" + result : result;
        }

        private void RefreshDirFiles()
        {
            _dirFiles.Clear();
            try
            {
                if (Directory.Exists(_currentDir))
                {
                    _dirFiles = Directory.GetFiles(_currentDir)
                        .OrderBy(f => Path.GetExtension(f))
                        .ThenBy(f => Path.GetFileName(f))
                        .ToList();
                }
            }
            catch { }
        }
    }
}
