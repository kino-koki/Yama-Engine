// ==================================================================================================
//  CodeEditorViewModel.cs — yama引擎代码编辑器视图模型
//
//  命名空间: YamaEngine.CodeEditing
//  依赖:     System.ComponentModel, System.Collections.ObjectModel, System.Collections.Generic
//
//  功能:
//    1. ErrorMark / ErrorSeverity       — 错误标记数据模型
//    2. CodeEditorViewModel             — INotifyPropertyChanged 视图模型
//         - 文档集合 / 当前文档 / 补全列表 / 签名提示
//         - 错误标记 / 断点标记 / 搜索替换状态
//    3. TextEditorControl               — WinUI 3 自定义控件描述性蓝图 (框架无关属性定义)
//         - 等宽字体 / 行号 / 语法着色 / 光标选择 / 滚动条 / 断点标记 / 搜索替换
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace YamaEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  搜索替换状态
    // ════════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════════
    //  搜索替换状态
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>搜索替换状态数据（搜索条件与结果集合；区别于 <see cref="SearchState"/> 状态枚举）。</summary>
    public sealed class SearchStateData
    {
        public string Query { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public bool IsRegex { get; set; }
        public bool MatchCase { get; set; }
        public bool MatchWholeWord { get; set; }
        public bool IsSearchActive { get; set; }
        public bool IsReplaceActive { get; set; }
        public int CurrentResultIndex { get; set; } = -1;
        public List<TextRange> Results { get; } = new();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CodeEditorViewModel — 视图模型
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class CodeEditorViewModelAdvanced : INotifyPropertyChanged
    {
        private CodeDocument? _currentDocument;
        private ObservableCollection<CompletionItem>? _activeCompletionList;
        private SignatureHelp? _activeSignatureHelp;
        private List<ErrorMark> _errorMarks = new();
        private List<int> _breakpointMarks = new();
        private bool _isCompletionActive;
        private bool _isSignatureHelpActive;
        private bool _isReadOnly;
        private string _fontName = "Cascadia Code";
        private double _fontSize = 14;
        private bool _showLineNumbers = true;
        private bool _showWhitespace;
        private bool _wordWrap;
        private readonly IntelliSenseProvider _intelliSense = new();
        private readonly SearchStateData _search = new();

        public ObservableCollection<CodeDocument> OpenedDocuments { get; } = new();

        public CodeDocument? CurrentDocument
        {
            get => _currentDocument;
            set
            {
                if (_currentDocument != value)
                {
                    _currentDocument = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CurrentFileName));
                    RefreshDiagnostics();
                    RefreshBreakpoints();
                }
            }
        }

        public string CurrentFileName => System.IO.Path.GetFileName(_currentDocument?.FilePath ?? "Untitled");

        public ObservableCollection<CompletionItem>? ActiveCompletionList
        {
            get => _activeCompletionList;
            private set { _activeCompletionList = value; OnPropertyChanged(); }
        }

        public SignatureHelp? ActiveSignatureHelp
        {
            get => _activeSignatureHelp;
            private set { _activeSignatureHelp = value; OnPropertyChanged(); }
        }

        public List<ErrorMark> ErrorMarks
        {
            get => _errorMarks;
            private set { _errorMarks = value; OnPropertyChanged(); }
        }

        public List<int> BreakpointMarks
        {
            get => _breakpointMarks;
            private set { _breakpointMarks = value; OnPropertyChanged(); }
        }

        public bool IsCompletionActive
        {
            get => _isCompletionActive;
            private set { _isCompletionActive = value; OnPropertyChanged(); }
        }

        public bool IsSignatureHelpActive
        {
            get => _isSignatureHelpActive;
            private set { _isSignatureHelpActive = value; OnPropertyChanged(); }
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set { _isReadOnly = value; OnPropertyChanged(); }
        }

        public string FontName
        {
            get => _fontName;
            set { _fontName = value; OnPropertyChanged(); }
        }

        public double FontSize
        {
            get => _fontSize;
            set { _fontSize = value; OnPropertyChanged(); }
        }

        public bool ShowLineNumbers
        {
            get => _showLineNumbers;
            set { _showLineNumbers = value; OnPropertyChanged(); }
        }

        public bool ShowWhitespace
        {
            get => _showWhitespace;
            set { _showWhitespace = value; OnPropertyChanged(); }
        }

        public bool WordWrap
        {
            get => _wordWrap;
            set { _wordWrap = value; OnPropertyChanged(); }
        }

        public SearchStateData Search => _search;
        public IntelliSenseProvider IntelliSense => _intelliSense;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // ── 文档管理 ─────────────────────────────────────────────────────────────

        public void OpenDocument(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var existing = OpenedDocuments.FirstOrDefault(d => d.FilePath == filePath);
            if (existing != null) { CurrentDocument = existing; return; }
            var text = System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : string.Empty;
            var doc = new CodeDocument(filePath, text);
            if (filePath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)) doc.Language = CodeLanguage.Hlsl;
            OpenedDocuments.Add(doc);
            CurrentDocument = doc;
        }

        public void NewDocument(string? filePath = null)
        {
            var doc = new CodeDocument(filePath ?? "Untitled.cs", string.Empty);
            OpenedDocuments.Add(doc);
            CurrentDocument = doc;
        }

        public bool CloseDocument(CodeDocument? doc)
        {
            if (doc == null) return false;
            if (doc.Modified)
            {
                // 可在此触发保存询问 (由视图层处理)
            }
            OpenedDocuments.Remove(doc);
            if (CurrentDocument == doc) CurrentDocument = OpenedDocuments.FirstOrDefault();
            return true;
        }

        public void SaveCurrentDocument()
        {
            CurrentDocument?.Save();
            OnPropertyChanged(nameof(CurrentDocument));
        }

        public void SaveAllDocuments()
        {
            foreach (var doc in OpenedDocuments) doc.Save();
        }

        // ── 智能感知 ─────────────────────────────────────────────────────────────

        public void RequestCompletion(int position)
        {
            if (CurrentDocument == null) { HideCompletion(); return; }
            var items = _intelliSense.GetCompletionItems(CurrentDocument, position);
            if (items.Count > 0)
            {
                ActiveCompletionList = new ObservableCollection<CompletionItem>(items);
                IsCompletionActive = true;
            }
            else HideCompletion();
        }

        public void HideCompletion()
        {
            ActiveCompletionList = null;
            IsCompletionActive = false;
        }

        public void RequestSignatureHelp(int position)
        {
            if (CurrentDocument == null) { HideSignatureHelp(); return; }
            var help = _intelliSense.GetSignatureHelp(CurrentDocument, position);
            if (help != null && help.IsActive)
            {
                ActiveSignatureHelp = help;
                IsSignatureHelpActive = true;
            }
            else HideSignatureHelp();
        }

        public void HideSignatureHelp()
        {
            ActiveSignatureHelp = null;
            IsSignatureHelpActive = false;
        }

        public void RequestQuickInfo(int position)
        {
            if (CurrentDocument == null) return;
            var info = _intelliSense.GetQuickInfo(CurrentDocument, position);
            if (info?.IsValid == true)
            {
                // 由视图层消费此信息 (例如显示 ToolTip)
                QuickInfoRequested?.Invoke(this, info);
            }
        }

        public event EventHandler<QuickInfo>? QuickInfoRequested;

        // ── 诊断与断点 ───────────────────────────────────────────────────────────

        public void RefreshDiagnostics()
        {
            if (CurrentDocument == null) { ErrorMarks = new List<ErrorMark>(); return; }
            var raw = _intelliSense.GetDiagnostics(CurrentDocument);
            var marks = raw.Select(d => new ErrorMark
            {
                Line = d.Span.Start.Line,
                StartColumn = d.Span.Start.Column,
                EndColumn = d.Span.End.Column,
                Message = d.Message,
                Severity = d.Severity switch { CodeDiagnosticSeverity.Warning => ErrorSeverity.Warning, CodeDiagnosticSeverity.Info => ErrorSeverity.Info, _ => ErrorSeverity.Error }
            }).ToList();
            ErrorMarks = marks;
        }

        public void RefreshBreakpoints()
        {
            if (CurrentDocument == null) { BreakpointMarks = new List<int>(); return; }
            // 由外部 DebugSession 提供断点数据; 此处仅演示刷新接口
            OnPropertyChanged(nameof(BreakpointMarks));
        }

        public void SetBreakpointMarks(IEnumerable<int> lines)
        {
            BreakpointMarks = lines.ToList();
        }

        // ── 格式化 ─────────────────────────────────────────────────────────────────

        public void FormatDocument()
        {
            if (CurrentDocument == null) return;
            var formatted = CSharpFormatter.Format(CurrentDocument.Text);
            CurrentDocument.ReplaceText(new TextRange(TextPosition.Zero, new TextPosition(CurrentDocument.LineCount, 0)), formatted);
        }

        public IReadOnlyList<FoldRange> GetFoldRanges()
        {
            if (CurrentDocument == null) return Array.Empty<FoldRange>();
            return CSharpFormatter.GetFoldRanges(CurrentDocument.Text);
        }

        // ── 搜索替换 ─────────────────────────────────────────────────────────────

        public void FindAll(string query)
        {
            Search.Query = query;
            Search.Results.Clear();
            if (CurrentDocument == null || string.IsNullOrEmpty(query)) return;
            var text = CurrentDocument.Text;
            int idx = 0;
            var comparison = Search.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            while ((idx = text.IndexOf(query, idx, comparison)) >= 0)
            {
                var start = CurrentDocument.OffsetToPosition(idx);
                var end = CurrentDocument.OffsetToPosition(idx + query.Length);
                Search.Results.Add(new TextRange(start, end));
                idx += query.Length;
            }
            Search.CurrentResultIndex = Search.Results.Count > 0 ? 0 : -1;
        }

        public void ReplaceCurrent(string replacement)
        {
            if (CurrentDocument == null || Search.CurrentResultIndex < 0 || Search.CurrentResultIndex >= Search.Results.Count) return;
            var range = Search.Results[Search.CurrentResultIndex];
            CurrentDocument.ReplaceText(range, replacement);
            FindAll(Search.Query); // 刷新结果
        }

        public void ReplaceAll(string replacement)
        {
            if (CurrentDocument == null || string.IsNullOrEmpty(Search.Query)) return;
            // 从后向前替换, 避免偏移问题
            for (int i = Search.Results.Count - 1; i >= 0; i--)
                CurrentDocument.ReplaceText(Search.Results[i], replacement);
            FindAll(Search.Query);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  TextEditorControl — WinUI 3 自定义控件描述性蓝图
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// WinUI 3 自定义文本编辑器控件蓝图。
    /// 以下为框架无关的属性/事件/接口定义, 供视图层实现时参考。
    /// 实现时应继承自 Microsoft.UI.Xaml.Controls.Control (WinUI 3) 或等效基类。
    /// </summary>
    public sealed class TextEditorControlDescription
    {
        // 字体与外观
        public string FontFamily { get; set; } = "Cascadia Code";
        public double FontSize { get; set; } = 14;
        public bool ShowLineNumbers { get; set; } = true;
        public bool ShowWhitespace { get; set; } = false;
        public bool WordWrap { get; set; } = false;
        public bool IsReadOnly { get; set; } = false;

        // 数据绑定
        public CodeDocument? Document { get; set; }
        public CodeEditorViewModelAdvanced? ViewModel { get; set; }

        // 着色与标记
        public List<(TextSegment Segment, System.Drawing.Color Color)> ColoredSegments { get; } = new();
        public List<ErrorMark> ErrorMarks { get; } = new();
        public List<int> BreakpointLineNumbers { get; } = new();

        // 光标与选择
        public TextPosition CursorPosition { get; set; } = TextPosition.Zero;
        public TextRange? SelectionRange { get; set; }

        // 滚动与布局
        public double ScrollOffsetX { get; set; }
        public double ScrollOffsetY { get; set; }
        public double LineHeight => FontSize * 1.4;
        public double CharWidth => FontSize * 0.6; // 近似等宽字符宽度

        // 事件 (由控件实现层映射到实际路由事件)
        public event EventHandler<TextPosition>? CursorPositionChanged;
        public event EventHandler<TextRange>? SelectionChanged;
        public event EventHandler<string>? TextChanged;
        public event EventHandler<int>? CaretPositionChanged; // 基于字符偏移
        public event EventHandler? BreakpointToggleRequested;
        public event EventHandler? CompletionRequested;
        public event EventHandler? SignatureHelpRequested;
        public event EventHandler? QuickInfoRequested;
        public event EventHandler? FindRequested;

        // 核心绘制逻辑描述 (由控件实现层在 OnDraw/OnRender 中调用)
        public void DrawDescription()
        {
            // 1. 绘制背景 (深色主题默认 #1E1E1E)
            // 2. 绘制行号栏 (宽度基于最大行号位数 * CharWidth + 边距)
            // 3. 逐行绘制文本, 应用 ColoredSegments 中的颜色分段
            // 4. 绘制光标 (竖线闪烁)
            // 5. 绘制选择高亮 (半透明背景色)
            // 6. 绘制断点标记 (行号栏左侧红点)
            // 7. 绘制错误波浪线 (红色下划线于 ErrorMarks 对应行)
            // 8. 绘制折叠指示器 (FoldRange 起止行左侧 +/-)
            // 9. 处理输入: 字符插入/删除、方向键、Ctrl+C/V/X/Z/Y
            // 10. 处理鼠标: 点击定位光标、拖拽选择、双击选词
            // 11. 处理滚动: 滚轮/拖拽滚动条更新 ScrollOffset
            // 12. 搜索替换: 高亮所有匹配项, 当前匹配项用不同背景色
        }

        // 公开命令 (控件实现层绑定快捷键)
        public void Undo() => Document?.Undo();
        public void Redo() => Document?.Redo();
        public void Cut() { /* 复制选区到剪贴板并删除 */ }
        public void Copy() { /* 复制选区到剪贴板 */ }
        public void Paste() { /* 从剪贴板插入 */ }
        public void SelectAll() { if (Document != null) SelectionRange = new TextRange(TextPosition.Zero, new TextPosition(Document.LineCount, 0)); }
        public void DeleteLine() { if (Document != null) Document.DeleteText(new TextRange(new TextPosition(CursorPosition.Line, 0), new TextPosition(CursorPosition.Line + 1, 0))); }
        public void ToggleBreakpointCurrentLine() => BreakpointToggleRequested?.Invoke(this, EventArgs.Empty);
        public void RequestCompletion() => CompletionRequested?.Invoke(this, EventArgs.Empty);
        public void RequestSignatureHelp() => SignatureHelpRequested?.Invoke(this, EventArgs.Empty);
        public void RequestQuickInfo() => QuickInfoRequested?.Invoke(this, EventArgs.Empty);
        public void ShowFind() => FindRequested?.Invoke(this, EventArgs.Empty);
        public void GoToDefinition() { /* F12: 调用 ViewModel.IntelliSense.GoToDefinition */ }
    }
}
