// ==================================================================================================
//  CodeEditorView.cs — 山雨引擎代码编辑器视图层
//
//  命名空间: ShanyuEngine.CodeEditing
//  依赖:     System.ComponentModel (INotifyPropertyChanged), System.Windows.Input (ICommand)
//
//  功能:
//    1. ErrorMark            — 错误标记 (行号/消息/严重性/波浪线范围)
//    2. EditorSettings       — 编辑器设置 (字体/缩进/换行/行号/空白显示等)
//    3. TextEditorControl    — 自定义编辑器控件逻辑层
//         - 等宽字体渲染支持 (Cascadia Code / Consolas)
//         - 行号显示
//         - 语法着色 (与 SyntaxHighlighter 集成)
//         - 光标和选择管理
//         - 滚动条 (垂直/水平)
//         - 断点标记区域
//         - 错误波浪线
//         - 搜索替换
//         - 代码折叠
//    4. CodeEditorViewModel  — MVVM ViewModel (INotifyPropertyChanged)
//         - CurrentDocument / OpenedDocuments
//         - ActiveCompletionList / ActiveSignatureHelp / ActiveQuickInfo
//         - ErrorMarks / BreakpointMarks
//         - 命令绑定 (保存/格式化/撤销/重做/搜索等)
//
//  说明:
//    本文件为纯逻辑层, 不依赖 WinUI 3 SDK。TextEditorControl 作为控件数据模型,
//    实际渲染由上层 WinUI UserControl (SwapChainPanel / Canvas) 绑定本类属性实现。
// ==================================================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ShanyuEngine.Scripting;

namespace ShanyuEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  错误标记
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>错误严重性级别。</summary>
    public enum ErrorSeverity
    {
        /// <summary>信息。</summary>
        Info,
        /// <summary>警告。</summary>
        Warning,
        /// <summary>错误。</summary>
        Error
    }

    /// <summary>
    /// 错误标记。记录源代码中某行的错误/警告信息, 用于编辑器边距图标和波浪线显示。
    /// </summary>
    public sealed class ErrorMark : INotifyPropertyChanged
    {
        private int _line;
        private string _message = string.Empty;
        private ErrorSeverity _severity;
        private int _startColumn;
        private int _endColumn;

        /// <summary>行号 (1 基)。</summary>
        public int Line
        {
            get => _line;
            set { if (_line != value) { _line = value; OnPropertyChanged(); } }
        }

        /// <summary>错误消息。</summary>
        public string Message
        {
            get => _message;
            set { if (_message != value) { _message = value ?? string.Empty; OnPropertyChanged(); } }
        }

        /// <summary>严重性。</summary>
        public ErrorSeverity Severity
        {
            get => _severity;
            set { if (_severity != value) { _severity = value; OnPropertyChanged(); } }
        }

        /// <summary>波浪线起始列 (0 基, -1 表示整行)。</summary>
        public int StartColumn
        {
            get => _startColumn;
            set { if (_startColumn != value) { _startColumn = value; OnPropertyChanged(); } }
        }

        /// <summary>波浪线结束列 (0 基, -1 表示行尾)。</summary>
        public int EndColumn
        {
            get => _endColumn;
            set { if (_endColumn != value) { _endColumn = value; OnPropertyChanged(); } }
        }

        public ErrorMark() { _startColumn = -1; _endColumn = -1; }

        public ErrorMark(int line, string message, ErrorSeverity severity)
        {
            _line = line;
            _message = message ?? string.Empty;
            _severity = severity;
            _startColumn = -1;
            _endColumn = -1;
        }

        public ErrorMark(int line, string message, ErrorSeverity severity, int startColumn, int endColumn)
        {
            _line = line;
            _message = message ?? string.Empty;
            _severity = severity;
            _startColumn = startColumn;
            _endColumn = endColumn;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  编辑器设置
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器字体设置。</summary>
    public sealed class EditorFontSettings : INotifyPropertyChanged
    {
        private string _fontFamily = "Cascadia Code";
        private double _fontSize = 14.0;
        private double _lineHeight = 20.0;

        /// <summary>等宽字体族 (推荐 Cascadia Code, 回退 Consolas)。</summary>
        public string FontFamily
        {
            get => _fontFamily;
            set { if (_fontFamily != value) { _fontFamily = value; OnPropertyChanged(); } }
        }

        /// <summary>字号 (px)。</summary>
        public double FontSize
        {
            get => _fontSize;
            set { if (Math.Abs(_fontSize - value) > 0.01) { _fontSize = value; OnPropertyChanged(); } }
        }

        /// <summary>行高 (px)。</summary>
        public double LineHeight
        {
            get => _lineHeight;
            set { if (Math.Abs(_lineHeight - value) > 0.01) { _lineHeight = value; OnPropertyChanged(); } }
        }

        /// <summary>字符宽度估算 (等宽字体 = FontSize * 0.6)。</summary>
        public double CharWidth => _fontSize * 0.6;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>编辑器显示与行为设置。</summary>
    public sealed class EditorSettings : INotifyPropertyChanged
    {
        private readonly EditorFontSettings _font = new();
        private int _tabSize = 4;
        private bool _insertSpaces = true;
        private bool _showLineNumbers = true;
        private bool _showWhitespace = false;
        private bool _wordWrap = false;
        private bool _showBreakpointMargin = true;
        private bool _showFoldMargin = true;
        private bool _highlightCurrentLine = true;
        private bool _autoIndent = true;
        private bool _autoBraceCompletion = true;
        private int _maxVisibleLines = 200;

        public EditorFontSettings Font => _font;

        /// <summary>Tab 宽度 (空格数)。</summary>
        public int TabSize
        {
            get => _tabSize;
            set { if (_tabSize != value) { _tabSize = Math.Max(1, value); OnPropertyChanged(); } }
        }

        /// <summary>是否用空格替代 Tab。</summary>
        public bool InsertSpaces
        {
            get => _insertSpaces;
            set { if (_insertSpaces != value) { _insertSpaces = value; OnPropertyChanged(); } }
        }

        public bool ShowLineNumbers
        {
            get => _showLineNumbers;
            set { if (_showLineNumbers != value) { _showLineNumbers = value; OnPropertyChanged(); } }
        }

        public bool ShowWhitespace
        {
            get => _showWhitespace;
            set { if (_showWhitespace != value) { _showWhitespace = value; OnPropertyChanged(); } }
        }

        public bool WordWrap
        {
            get => _wordWrap;
            set { if (_wordWrap != value) { _wordWrap = value; OnPropertyChanged(); } }
        }

        public bool ShowBreakpointMargin
        {
            get => _showBreakpointMargin;
            set { if (_showBreakpointMargin != value) { _showBreakpointMargin = value; OnPropertyChanged(); } }
        }

        public bool ShowFoldMargin
        {
            get => _showFoldMargin;
            set { if (_showFoldMargin != value) { _showFoldMargin = value; OnPropertyChanged(); } }
        }

        public bool HighlightCurrentLine
        {
            get => _highlightCurrentLine;
            set { if (_highlightCurrentLine != value) { _highlightCurrentLine = value; OnPropertyChanged(); } }
        }

        public bool AutoIndent
        {
            get => _autoIndent;
            set { if (_autoIndent != value) { _autoIndent = value; OnPropertyChanged(); } }
        }

        public bool AutoBraceCompletion
        {
            get => _autoBraceCompletion;
            set { if (_autoBraceCompletion != value) { _autoBraceCompletion = value; OnPropertyChanged(); } }
        }

        /// <summary>最大可见行数 (虚拟化用)。</summary>
        public int MaxVisibleLines
        {
            get => _maxVisibleLines;
            set { if (_maxVisibleLines != value) { _maxVisibleLines = Math.Max(10, value); OnPropertyChanged(); } }
        }

        /// <summary>获取缩进字符串。</summary>
        public string GetIndentString()
            => InsertSpaces ? new string(' ', TabSize) : "\t";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  视口与渲染信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器视口信息。</summary>
    public sealed class ViewportInfo : INotifyPropertyChanged
    {
        private int _firstVisibleLine = 0;   // 0 基
        private int _visibleLineCount = 50;
        private int _totalLines = 1;
        private double _scrollX = 0;
        private double _scrollY = 0;
        private double _maxScrollX = 0;
        private double _maxScrollY = 0;

        public int FirstVisibleLine
        {
            get => _firstVisibleLine;
            set { if (_firstVisibleLine != value) { _firstVisibleLine = value; OnPropertyChanged(); } }
        }

        public int VisibleLineCount
        {
            get => _visibleLineCount;
            set { if (_visibleLineCount != value) { _visibleLineCount = value; OnPropertyChanged(); } }
        }

        public int TotalLines
        {
            get => _totalLines;
            set { if (_totalLines != value) { _totalLines = value; OnPropertyChanged(); } }
        }

        public double ScrollX
        {
            get => _scrollX;
            set { if (Math.Abs(_scrollX - value) > 0.01) { _scrollX = value; OnPropertyChanged(); } }
        }

        public double ScrollY
        {
            get => _scrollY;
            set { if (Math.Abs(_scrollY - value) > 0.01) { _scrollY = value; OnPropertyChanged(); } }
        }

        public double MaxScrollX
        {
            get => _maxScrollX;
            set { if (Math.Abs(_maxScrollX - value) > 0.01) { _maxScrollX = value; OnPropertyChanged(); } }
        }

        public double MaxScrollY
        {
            get => _maxScrollY;
            set { if (Math.Abs(_maxScrollY - value) > 0.01) { _maxScrollY = value; OnPropertyChanged(); } }
        }

        /// <summary>垂直滚动比例 (0~1)。</summary>
        public double VerticalScrollRatio
            => _maxScrollY > 0 ? _scrollY / _maxScrollY : 0;

        /// <summary>水平滚动比例 (0~1)。</summary>
        public double HorizontalScrollRatio
            => _maxScrollX > 0 ? _scrollX / _maxScrollX : 0;

        /// <summary>最后可见行 (0 基, 不含)。</summary>
        public int LastVisibleLineExclusive
            => Math.Min(_firstVisibleLine + _visibleLineCount, _totalLines);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>可见行渲染信息 (供上层绘制使用)。</summary>
    public sealed class VisibleLineInfo
    {
        /// <summary>行号 (1 基)。</summary>
        public int LineNumber { get; init; }

        /// <summary>行文本 (0 基行索引对应的 TextLines[i])。</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>该行的着色段。</summary>
        public IReadOnlyList<ColoredSegment> Segments { get; init; } = Array.Empty<ColoredSegment>();

        /// <summary>该行是否有断点。</summary>
        public bool HasBreakpoint { get; init; }

        /// <summary>该行是否有错误标记。</summary>
        public bool HasError { get; init; }

        /// <summary>该行的错误标记列表。</summary>
        public IReadOnlyList<ErrorMark> Errors { get; init; } = Array.Empty<ErrorMark>();

        /// <summary>是否为当前光标所在行。</summary>
        public bool IsCurrentLine { get; init; }

        /// <summary>是否在折叠区域内 (被折叠隐藏)。</summary>
        public bool IsFolded { get; init; }

        /// <summary>行首缩进字符串。</summary>
        public string Indent { get; init; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  搜索替换
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>搜索选项。</summary>
    public sealed class SearchOptions : INotifyPropertyChanged
    {
        private string _searchText = string.Empty;
        private string _replaceText = string.Empty;
        private bool _matchCase = false;
        private bool _wholeWord = false;
        private bool _useRegex = false;
        private bool _searchUp = false;

        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); } }
        }

        public string ReplaceText
        {
            get => _replaceText;
            set { if (_replaceText != value) { _replaceText = value; OnPropertyChanged(); } }
        }

        public bool MatchCase
        {
            get => _matchCase;
            set { if (_matchCase != value) { _matchCase = value; OnPropertyChanged(); } }
        }

        public bool WholeWord
        {
            get => _wholeWord;
            set { if (_wholeWord != value) { _wholeWord = value; OnPropertyChanged(); } }
        }

        public bool UseRegex
        {
            get => _useRegex;
            set { if (_useRegex != value) { _useRegex = value; OnPropertyChanged(); } }
        }

        public bool SearchUp
        {
            get => _searchUp;
            set { if (_searchUp != value) { _searchUp = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>搜索结果。</summary>
    public sealed class SearchResult
    {
        public int Line { get; init; }       // 0 基
        public int Column { get; init; }     // 0 基
        public int Length { get; init; }
        public string MatchText { get; init; } = string.Empty;

        public TextPosition ToPosition() => new(Line, Column);
        public TextRange ToRange() => new(new TextPosition(Line, Column), new TextPosition(Line, Column + Length));
    }

    /// <summary>搜索状态。</summary>

    // ════════════════════════════════════════════════════════════════════════════
    //  事件参数
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>断点切换事件参数。</summary>
    public sealed class BreakpointToggleEventArgs : EventArgs
    {
        public int Line { get; init; }  // 1 基
        public bool Enabled { get; init; }
    }

    /// <summary>光标移动事件参数。</summary>
    public sealed class CursorMovedEventArgs : EventArgs
    {
        public TextPosition OldPosition { get; init; }
        public TextPosition NewPosition { get; init; }
    }

    /// <summary>文本变更事件参数。</summary>
    public sealed class TextChangedEventArgs : EventArgs
    {
        public TextRange ChangedRange { get; init; }
        public string OldText { get; init; } = string.Empty;
        public string NewText { get; init; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  RelayCommand — 简单 ICommand 实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>简单的 ICommand 实现, 用于 ViewModel 命令绑定。</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = _ => execute();
            _canExecute = canExecute != null ? _ => canExecute() : null;
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  TextEditorControl — 编辑器控件逻辑层
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 代码编辑器控件逻辑层。管理文档、光标、选择、滚动、断点、搜索等状态,
    /// 为上层 WinUI 渲染提供数据。不继承任何 UI 类, 纯逻辑控制器。
    /// </summary>
    public sealed class TextEditorControl : INotifyPropertyChanged
    {
        // ── 内部状态 ──────────────────────────────────────────────────────────────

        private CodeDocument? _document;
        private readonly EditorSettings _settings = new();
        private readonly ViewportInfo _viewport = new();
        private readonly HashSet<int> _breakpointLines = new(); // 1 基行号
        private readonly Dictionary<int, List<ErrorMark>> _errorMarks = new(); // 1 基行号
        private readonly HashSet<int> _foldedStartLines = new(); // 折叠区域起始行 (1 基)
        private List<FoldRange> _foldRanges = new();
        private CodeLanguage _language = CodeLanguage.CSharp;
        private SearchOptions _searchOptions = new();
        private SearchState _searchState = SearchState.Idle;
        private List<SearchResult> _searchResults = new();
        private int _currentSearchIndex = -1;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>当前编辑的代码文档。</summary>
        public CodeDocument? Document
        {
            get => _document;
            set
            {
                if (_document != value)
                {
                    _document = value;
                    OnDocumentChanged();
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>编辑器设置。</summary>
        public EditorSettings Settings => _settings;

        /// <summary>视口信息。</summary>
        public ViewportInfo Viewport => _viewport;

        /// <summary>当前语言。</summary>
        public CodeLanguage Language
        {
            get => _language;
            set { if (_language != value) { _language = value; OnPropertyChanged(); InvalidateHighlighting(); } }
        }

        /// <summary>搜索选项。</summary>
        public SearchOptions SearchOptions
        {
            get => _searchOptions;
            set { if (_searchOptions != value) { _searchOptions = value; OnPropertyChanged(); } }
        }

        /// <summary>搜索状态。</summary>
        public SearchState SearchState
        {
            get => _searchState;
            private set { if (_searchState != value) { _searchState = value; OnPropertyChanged(); } }
        }

        /// <summary>搜索结果数量。</summary>
        public int SearchResultCount => _searchResults.Count;

        /// <summary>当前搜索结果索引 (0 基, -1 表示无)。</summary>
        public int CurrentSearchIndex
        {
            get => _currentSearchIndex;
            private set { if (_currentSearchIndex != value) { _currentSearchIndex = value; OnPropertyChanged(); } }
        }

        /// <summary>断点行集合 (1 基, 只读)。</summary>
        public IReadOnlyCollection<int> BreakpointLines => _breakpointLines;

        /// <summary>折叠范围列表。</summary>
        public IReadOnlyList<FoldRange> FoldRanges => _foldRanges;

        /// <summary>光标位置。</summary>
        public TextPosition Cursor => _document?.Cursor ?? default;

        /// <summary>选择范围。</summary>
        public TextRange? Selection => _document?.Selection;

        /// <summary>是否有选择文本。</summary>
        public bool HasSelection => _document?.Selection != null;

        /// <summary>是否可撤销。</summary>
        public bool CanUndo => _document != null && _document.UndoStack.Count > 0;

        /// <summary>是否可重做。</summary>
        public bool CanRedo => _document != null && _document.RedoStack.Count > 0;

        /// <summary>行号边距宽度 (字符数)。</summary>
        public int LineNumberMarginWidth
            => _settings.ShowLineNumbers ? Math.Max(3, (_document?.LineCount ?? 1).ToString().Length) : 0;

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>光标移动事件。</summary>
        public event EventHandler<CursorMovedEventArgs>? CursorMoved;

        /// <summary>文本变更事件。</summary>
        public event EventHandler<TextChangedEventArgs>? TextChanged;

        /// <summary>断点切换事件。</summary>
        public event EventHandler<BreakpointToggleEventArgs>? BreakpointToggled;

        /// <summary>属性变更事件。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ── 文档管理 ──────────────────────────────────────────────────────────────

        private void OnDocumentChanged()
        {
            _breakpointLines.Clear();
            _errorMarks.Clear();
            _foldedStartLines.Clear();
            _searchResults.Clear();
            _currentSearchIndex = -1;
            SearchState = SearchState.Idle;

            if (_document != null)
            {
                _viewport.TotalLines = _document.LineCount;
                EnsureCursorVisible();
                UpdateFoldRanges();
            }
            else
            {
                _viewport.TotalLines = 1;
            }

            InvalidateHighlighting();
        }

        /// <summary>设置文档内容。</summary>
        public void SetDocument(CodeDocument? document)
        {
            Document = document;
        }

        /// <summary>从文件加载文档。</summary>
        public void LoadFile(string filePath)
        {
            var text = System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : string.Empty;
            var doc = new CodeDocument(filePath, text);
            Document = doc;
        }

        /// <summary>保存当前文档。</summary>
        public bool Save()
        {
            if (_document == null) return false;
            _document.Save();
            return true;
        }

        // ── 文本编辑 ──────────────────────────────────────────────────────────────

        /// <summary>在光标处插入文本。</summary>
        public void TypeText(string text)
        {
            if (_document == null || string.IsNullOrEmpty(text)) return;

            var oldCursor = _document.Cursor;
            var start = _document.Selection?.Start ?? _document.Cursor;
            var end = _document.Selection?.End ?? _document.Cursor;
            var range = new TextRange(start, end);
            var oldText = _document.GetText(range);

            _document.ReplaceText(range, text);

            _viewport.TotalLines = _document.LineCount;
            UpdateFoldRanges();
            TextChanged?.Invoke(this, new TextChangedEventArgs
            {
                ChangedRange = new TextRange(start, _document.Cursor),
                OldText = oldText,
                NewText = text
            });
            EnsureCursorVisible();
            InvalidateHighlighting();
        }

        /// <summary>删除选择区域或光标前的字符 (Backspace)。</summary>
        public void Backspace()
        {
            if (_document == null) return;

            TextRange range;
            if (_document.Selection != null)
            {
                range = _document.Selection.Value;
            }
            else
            {
                if (_document.Cursor.Column > 0)
                {
                    range = new TextRange(
                        new TextPosition(_document.Cursor.Line, _document.Cursor.Column - 1),
                        _document.Cursor);
                }
                else if (_document.Cursor.Line > 0)
                {
                    var prevLineLen = _document.TextLines[_document.Cursor.Line - 1].Length;
                    range = new TextRange(
                        new TextPosition(_document.Cursor.Line - 1, prevLineLen),
                        _document.Cursor);
                }
                else return;
            }

            var oldText = _document.GetText(range);
            _document.ReplaceText(range, string.Empty);
            _viewport.TotalLines = _document.LineCount;
            UpdateFoldRanges();
            TextChanged?.Invoke(this, new TextChangedEventArgs
            {
                ChangedRange = new TextRange(_document.Cursor, _document.Cursor),
                OldText = oldText,
                NewText = string.Empty
            });
            EnsureCursorVisible();
            InvalidateHighlighting();
        }

        /// <summary>删除选择区域或光标后的字符 (Delete)。</summary>
        public void Delete()
        {
            if (_document == null) return;

            TextRange range;
            if (_document.Selection != null)
            {
                range = _document.Selection.Value;
            }
            else
            {
                var line = _document.TextLines[_document.Cursor.Line];
                if (_document.Cursor.Column < line.Length)
                {
                    range = new TextRange(_document.Cursor,
                        new TextPosition(_document.Cursor.Line, _document.Cursor.Column + 1));
                }
                else if (_document.Cursor.Line < _document.LineCount - 1)
                {
                    range = new TextRange(_document.Cursor,
                        new TextPosition(_document.Cursor.Line + 1, 0));
                }
                else return;
            }

            var oldText = _document.GetText(range);
            _document.ReplaceText(range, string.Empty);
            _viewport.TotalLines = _document.LineCount;
            UpdateFoldRanges();
            TextChanged?.Invoke(this, new TextChangedEventArgs
            {
                ChangedRange = new TextRange(_document.Cursor, _document.Cursor),
                OldText = oldText,
                NewText = string.Empty
            });
            EnsureCursorVisible();
            InvalidateHighlighting();
        }

        /// <summary>插入换行 (带自动缩进)。</summary>
        public void InsertNewLine()
        {
            if (_document == null) return;

            string indent = string.Empty;
            if (_settings.AutoIndent)
            {
                var currentLine = _document.TextLines[_document.Cursor.Line];
                indent = _document.GetLineIndent(_document.Cursor.Line);

                // 如果行尾是 {, 增加一级缩进
                var trimmed = currentLine.TrimEnd();
                if (trimmed.EndsWith("{"))
                    indent += _settings.GetIndentString();
            }

            TypeText("\n" + indent);

            // 自动补全大括号
            if (_settings.AutoBraceCompletion)
            {
                var line = _document.TextLines[_document.Cursor.Line];
                var trimmed = line.TrimStart();
                if (trimmed == indent + "}")
                {
                    // 在 } 前插入换行 + 缩进
                    var insertPos = new TextPosition(_document.Cursor.Line, line.Length - 1);
                    _document.Cursor = insertPos;
                    _document.Selection = null;
                    var extraIndent = indent.Length >= _settings.GetIndentString().Length
                        ? indent.Substring(0, indent.Length - _settings.GetIndentString().Length)
                        : indent;
                    TypeText("\n" + extraIndent + _settings.GetIndentString());
                    _document.Cursor = new TextPosition(insertPos.Line, insertPos.Column);
                    _document.Selection = null;
                }
            }
        }

        /// <summary>Tab 键处理 (插入缩进)。</summary>
        public void InsertTab()
        {
            if (_document == null) return;

            if (_document.Selection != null)
            {
                // 多行选择: 整体缩进
                var sel = _document.Selection.Value;
                int startLine = Math.Min(sel.Start.Line, sel.End.Line);
                int endLine = Math.Max(sel.Start.Line, sel.End.Line);
                var indentStr = _settings.GetIndentString();

                var lines = _document.TextLines.ToList();
                for (int i = startLine; i <= endLine; i++)
                    lines[i] = indentStr + lines[i];
                _document.ApplyEdit(0, _document.Text.Length, string.Join("\n", lines));
                InvalidateHighlighting();
            }
            else
            {
                TypeText(_settings.GetIndentString());
            }
        }

        /// <summary>Shift+Tab: 减少缩进。</summary>
        public void RemoveIndent()
        {
            if (_document == null) return;

            var sel = _document.Selection;
            int startLine = sel?.Start.Line ?? _document.Cursor.Line;
            int endLine = sel?.End.Line ?? _document.Cursor.Line;
            if (startLine > endLine) (startLine, endLine) = (endLine, startLine);

            var indentStr = _settings.GetIndentString();
            var lines = _document.TextLines.ToList();
            bool changed = false;
            for (int i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                if (line.StartsWith("\t"))
                {
                    lines[i] = line.Substring(1);
                    changed = true;
                }
                else if (line.StartsWith(indentStr))
                {
                    lines[i] = line.Substring(indentStr.Length);
                    changed = true;
                }
                else if (line.StartsWith(" "))
                {
                    int spaces = 0;
                    while (spaces < line.Length && spaces < _settings.TabSize && line[spaces] == ' ') spaces++;
                    lines[i] = line.Substring(spaces);
                    changed = true;
                }
            }
            if (changed)
            {
                _document.ApplyEdit(0, _document.Text.Length, string.Join("\n", lines));
                InvalidateHighlighting();
            }
        }

        // ── 撤销/重做 ─────────────────────────────────────────────────────────────

        public void Undo()
        {
            _document?.Undo();
            if (_document != null) _viewport.TotalLines = _document.LineCount;
            EnsureCursorVisible();
            InvalidateHighlighting();
        }

        public void Redo()
        {
            _document?.Redo();
            if (_document != null) _viewport.TotalLines = _document.LineCount;
            EnsureCursorVisible();
            InvalidateHighlighting();
        }

        // ── 光标移动 ──────────────────────────────────────────────────────────────

        /// <summary>移动光标。</summary>
        public void MoveCursor(TextPosition position, bool extendSelection = false)
        {
            if (_document == null) return;

            var old = _document.Cursor;
            position = ClampPosition(position);

            if (extendSelection)
            {
                if (_document.Selection == null)
                    _document.Selection = new TextRange(old, position);
                else
                    _document.Selection = new TextRange(_document.Selection.Value.Start, position);
            }
            else
            {
                _document.Selection = null;
            }

            _document.Cursor = position;
            CursorMoved?.Invoke(this, new CursorMovedEventArgs { OldPosition = old, NewPosition = position });
            EnsureCursorVisible();
            OnPropertyChanged(nameof(Cursor));
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(HasSelection));
        }

        /// <summary>光标左移。</summary>
        public void MoveLeft(bool extendSelection = false)
        {
            if (_document == null) return;
            var pos = _document.Cursor;
            if (pos.Column > 0)
                pos = new TextPosition(pos.Line, pos.Column - 1);
            else if (pos.Line > 0)
                pos = new TextPosition(pos.Line - 1, _document.TextLines[pos.Line - 1].Length);
            MoveCursor(pos, extendSelection);
        }

        /// <summary>光标右移。</summary>
        public void MoveRight(bool extendSelection = false)
        {
            if (_document == null) return;
            var pos = _document.Cursor;
            if (pos.Column < _document.TextLines[pos.Line].Length)
                pos = new TextPosition(pos.Line, pos.Column + 1);
            else if (pos.Line < _document.LineCount - 1)
                pos = new TextPosition(pos.Line + 1, 0);
            MoveCursor(pos, extendSelection);
        }

        /// <summary>光标上移。</summary>
        public void MoveUp(bool extendSelection = false)
        {
            if (_document == null) return;
            var pos = _document.Cursor;
            if (pos.Line > 0)
            {
                int col = Math.Min(pos.Column, _document.TextLines[pos.Line - 1].Length);
                pos = new TextPosition(pos.Line - 1, col);
            }
            MoveCursor(pos, extendSelection);
        }

        /// <summary>光标下移。</summary>
        public void MoveDown(bool extendSelection = false)
        {
            if (_document == null) return;
            var pos = _document.Cursor;
            if (pos.Line < _document.LineCount - 1)
            {
                int col = Math.Min(pos.Column, _document.TextLines[pos.Line + 1].Length);
                pos = new TextPosition(pos.Line + 1, col);
            }
            MoveCursor(pos, extendSelection);
        }

        /// <summary>光标移到行首。</summary>
        public void MoveToLineStart(bool extendSelection = false)
        {
            if (_document == null) return;
            var line = _document.TextLines[_document.Cursor.Line];
            int firstNonWs = 0;
            while (firstNonWs < line.Length && (line[firstNonWs] == ' ' || line[firstNonWs] == '\t')) firstNonWs++;
            int target = _document.Cursor.Column == firstNonWs && firstNonWs > 0 ? 0 : firstNonWs;
            MoveCursor(new TextPosition(_document.Cursor.Line, target), extendSelection);
        }

        /// <summary>光标移到行尾。</summary>
        public void MoveToLineEnd(bool extendSelection = false)
        {
            if (_document == null) return;
            MoveCursor(new TextPosition(_document.Cursor.Line, _document.TextLines[_document.Cursor.Line].Length), extendSelection);
        }

        /// <summary>光标移到文档开头。</summary>
        public void MoveToDocumentStart(bool extendSelection = false)
            => MoveCursor(new TextPosition(0, 0), extendSelection);

        /// <summary>光标移到文档末尾。</summary>
        public void MoveToDocumentEnd(bool extendSelection = false)
        {
            if (_document == null) return;
            MoveCursor(new TextPosition(_document.LineCount - 1,
                _document.TextLines[_document.LineCount - 1].Length), extendSelection);
        }

        /// <summary>光标按页上移。</summary>
        public void MovePageUp(bool extendSelection = false)
        {
            if (_document == null) return;
            int delta = Math.Max(1, _viewport.VisibleLineCount - 1);
            int newLine = Math.Max(0, _document.Cursor.Line - delta);
            int col = Math.Min(_document.Cursor.Column, _document.TextLines[newLine].Length);
            MoveCursor(new TextPosition(newLine, col), extendSelection);
        }

        /// <summary>光标按页下移。</summary>
        public void MovePageDown(bool extendSelection = false)
        {
            if (_document == null) return;
            int delta = Math.Max(1, _viewport.VisibleLineCount - 1);
            int newLine = Math.Min(_document.LineCount - 1, _document.Cursor.Line + delta);
            int col = Math.Min(_document.Cursor.Column, _document.TextLines[newLine].Length);
            MoveCursor(new TextPosition(newLine, col), extendSelection);
        }

        // ── 选择 ──────────────────────────────────────────────────────────────────

        /// <summary>全选。</summary>
        public void SelectAll()
        {
            if (_document == null || _document.LineCount == 0) return;
            var end = new TextPosition(_document.LineCount - 1,
                _document.TextLines[_document.LineCount - 1].Length);
            _document.Selection = new TextRange(new TextPosition(0, 0), end);
            _document.Cursor = end;
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(HasSelection));
        }

        /// <summary>选择当前行。</summary>
        public void SelectLine()
        {
            if (_document == null) return;
            var start = new TextPosition(_document.Cursor.Line, 0);
            var end = new TextPosition(_document.Cursor.Line, _document.TextLines[_document.Cursor.Line].Length);
            _document.Selection = new TextRange(start, end);
            _document.Cursor = end;
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(HasSelection));
        }

        /// <summary>选择指定范围。</summary>
        public void SelectRange(TextRange range)
        {
            if (_document == null) return;
            _document.Selection = range;
            _document.Cursor = range.End;
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(HasSelection));
        }

        /// <summary>清除选择。</summary>
        public void ClearSelection()
        {
            if (_document == null) return;
            _document.Selection = null;
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(HasSelection));
        }

        /// <summary>获取选中文本。</summary>
        public string GetSelectedText()
            => _document?.Selection != null ? _document.GetText(_document.Selection.Value) : string.Empty;

        /// <summary>复制选中文本到剪贴板 (返回文本, 由上层设置剪贴板)。</summary>
        public string? Copy()
        {
            if (_document?.Selection == null) return null;
            return GetSelectedText();
        }

        /// <summary>剪切选中文本 (返回文本并删除)。</summary>
        public string? Cut()
        {
            if (_document?.Selection == null) return null;
            var text = GetSelectedText();
            Delete();
            return text;
        }

        /// <summary>粘贴文本。</summary>
        public void Paste(string text)
        {
            if (!string.IsNullOrEmpty(text))
                TypeText(text);
        }

        // ── 滚动 ──────────────────────────────────────────────────────────────────

        /// <summary>确保光标在可见区域内。</summary>
        public void EnsureCursorVisible()
        {
            if (_document == null) return;

            int cursorLine = _document.Cursor.Line;

            if (cursorLine < _viewport.FirstVisibleLine)
            {
                _viewport.FirstVisibleLine = cursorLine;
            }
            else if (cursorLine >= _viewport.FirstVisibleLine + _viewport.VisibleLineCount)
            {
                _viewport.FirstVisibleLine = cursorLine - _viewport.VisibleLineCount + 1;
            }

            // 水平滚动 (基于字符宽度估算)
            double charWidth = _settings.Font.CharWidth;
            double cursorX = _document.Cursor.Column * charWidth;
            double viewWidth = charWidth * 80; // 假设视口宽度约80字符

            if (cursorX < _viewport.ScrollX)
                _viewport.ScrollX = cursorX;
            else if (cursorX >= _viewport.ScrollX + viewWidth)
                _viewport.ScrollX = cursorX - viewWidth + charWidth;

            UpdateScrollLimits();
        }

        /// <summary>滚动到指定行。</summary>
        public void ScrollToLine(int line)
        {
            if (_document == null) return;
            line = Math.Max(0, Math.Min(line, _document.LineCount - 1));
            _viewport.FirstVisibleLine = line;
        }

        /// <summary>滚动使指定行居中。</summary>
        public void ScrollToCenter(int line)
        {
            if (_document == null) return;
            line = Math.Max(0, Math.Min(line, _document.LineCount - 1));
            _viewport.FirstVisibleLine = Math.Max(0, line - _viewport.VisibleLineCount / 2);
        }

        /// <summary>垂直滚动。</summary>
        public void ScrollVertical(double delta)
        {
            double newY = Math.Max(0, Math.Min(_viewport.MaxScrollY, _viewport.ScrollY + delta));
            _viewport.ScrollY = newY;
            _viewport.FirstVisibleLine = (int)(newY / _settings.Font.LineHeight);
        }

        private void UpdateScrollLimits()
        {
            if (_document == null) return;
            double lineHeight = _settings.Font.LineHeight;
            double charWidth = _settings.Font.CharWidth;

            _viewport.MaxScrollY = Math.Max(0, _document.LineCount * lineHeight - _viewport.VisibleLineCount * lineHeight);
            int maxLineLength = 0;
            foreach (var line in _document.TextLines)
                maxLineLength = Math.Max(maxLineLength, line.Length);
            _viewport.MaxScrollX = Math.Max(0, maxLineLength * charWidth - charWidth * 80);
        }

        // ── 断点管理 ──────────────────────────────────────────────────────────────

        /// <summary>切换断点。</summary>
        public void ToggleBreakpoint(int line)
        {
            if (line < 1) return;
            bool enabled;
            if (_breakpointLines.Contains(line))
            {
                _breakpointLines.Remove(line);
                enabled = false;
            }
            else
            {
                _breakpointLines.Add(line);
                enabled = true;
            }
            BreakpointToggled?.Invoke(this, new BreakpointToggleEventArgs { Line = line, Enabled = enabled });
            OnPropertyChanged(nameof(BreakpointLines));
        }

        /// <summary>设置断点。</summary>
        public void SetBreakpoint(int line)
        {
            if (line < 1) return;
            if (_breakpointLines.Add(line))
            {
                BreakpointToggled?.Invoke(this, new BreakpointToggleEventArgs { Line = line, Enabled = true });
                OnPropertyChanged(nameof(BreakpointLines));
            }
        }

        /// <summary>移除断点。</summary>
        public void RemoveBreakpoint(int line)
        {
            if (_breakpointLines.Remove(line))
            {
                BreakpointToggled?.Invoke(this, new BreakpointToggleEventArgs { Line = line, Enabled = false });
                OnPropertyChanged(nameof(BreakpointLines));
            }
        }

        /// <summary>清除所有断点。</summary>
        public void ClearAllBreakpoints()
        {
            var lines = _breakpointLines.ToList();
            _breakpointLines.Clear();
            foreach (var line in lines)
                BreakpointToggled?.Invoke(this, new BreakpointToggleEventArgs { Line = line, Enabled = false });
            OnPropertyChanged(nameof(BreakpointLines));
        }

        /// <summary>检查指定行是否有断点。</summary>
        public bool HasBreakpoint(int line) => _breakpointLines.Contains(line);

        // ── 错误标记管理 ──────────────────────────────────────────────────────────

        /// <summary>添加错误标记。</summary>
        public void AddErrorMark(ErrorMark mark)
        {
            if (!_errorMarks.TryGetValue(mark.Line, out var list))
            {
                list = new List<ErrorMark>();
                _errorMarks[mark.Line] = list;
            }
            list.Add(mark);
        }

        /// <summary>设置错误标记列表 (替换全部)。</summary>
        public void SetErrorMarks(IEnumerable<ErrorMark> marks)
        {
            _errorMarks.Clear();
            foreach (var mark in marks)
            {
                if (!_errorMarks.TryGetValue(mark.Line, out var list))
                {
                    list = new List<ErrorMark>();
                    _errorMarks[mark.Line] = list;
                }
                list.Add(mark);
            }
        }

        /// <summary>获取指定行的错误标记。</summary>
        public IReadOnlyList<ErrorMark> GetErrorMarks(int line)
            => _errorMarks.TryGetValue(line, out var list)
                ? list
                : Array.Empty<ErrorMark>();

        /// <summary>获取所有错误标记。</summary>
        public IReadOnlyList<ErrorMark> GetAllErrorMarks()
            => _errorMarks.Values.SelectMany(list => list).ToList();

        /// <summary>清除所有错误标记。</summary>
        public void ClearErrorMarks() => _errorMarks.Clear();

        // ── 代码折叠 ──────────────────────────────────────────────────────────────

        /// <summary>更新折叠范围。</summary>
        public void UpdateFoldRanges()
        {
            if (_document == null || string.IsNullOrEmpty(_document.Text))
            {
                _foldRanges = new List<FoldRange>();
                return;
            }

            _foldRanges = CSharpFormatter.GetFoldRanges(_document.Text).ToList();
        }

        /// <summary>切换折叠状态。</summary>
        public void ToggleFold(int line)
        {
            if (_foldedStartLines.Contains(line))
                _foldedStartLines.Remove(line);
            else
                _foldedStartLines.Add(line);
        }

        /// <summary>检查行是否在折叠区域内 (被隐藏)。</summary>
        public bool IsLineFolded(int line)
        {
            foreach (var startLine in _foldedStartLines)
            {
                var fold = _foldRanges.FirstOrDefault(f => f.StartLine == startLine);
                if (fold != null && line > fold.StartLine && line <= fold.EndLine)
                    return true;
            }
            return false;
        }

        /// <summary>全部展开。</summary>
        public void UnfoldAll() => _foldedStartLines.Clear();

        // ── 搜索替换 ──────────────────────────────────────────────────────────────

        /// <summary>执行搜索。</summary>
        public void Search()
        {
            _searchResults.Clear();
            CurrentSearchIndex = -1;

            if (_document == null || string.IsNullOrEmpty(_searchOptions.SearchText))
            {
                SearchState = SearchState.Idle;
                OnPropertyChanged(nameof(SearchResultCount));
                return;
            }

            SearchState = SearchState.Searching;

            try
            {
                var pattern = BuildSearchPattern();
                var regex = new Regex(pattern, _searchOptions.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

                for (int i = 0; i < _document.LineCount; i++)
                {
                    var line = _document.TextLines[i];
                    foreach (Match m in regex.Matches(line))
                    {
                        _searchResults.Add(new SearchResult
                        {
                            Line = i,
                            Column = m.Index,
                            Length = m.Length,
                            MatchText = m.Value
                        });
                    }
                }

                if (_searchResults.Count > 0)
                {
                    SearchState = SearchState.Found;
                    CurrentSearchIndex = 0;
                    NavigateToSearchResult(0);
                }
                else
                {
                    SearchState = SearchState.NotFound;
                }
            }
            catch (ArgumentException)
            {
                SearchState = SearchState.NotFound;
            }

            OnPropertyChanged(nameof(SearchResultCount));
        }

        /// <summary>查找下一个。</summary>
        public void FindNext()
        {
            if (_searchResults.Count == 0) return;
            CurrentSearchIndex = (CurrentSearchIndex + 1) % _searchResults.Count;
            NavigateToSearchResult(CurrentSearchIndex);
        }

        /// <summary>查找上一个。</summary>
        public void FindPrevious()
        {
            if (_searchResults.Count == 0) return;
            CurrentSearchIndex = (CurrentSearchIndex - 1 + _searchResults.Count) % _searchResults.Count;
            NavigateToSearchResult(CurrentSearchIndex);
        }

        /// <summary>替换当前匹配。</summary>
        public void ReplaceCurrent()
        {
            if (_document == null || CurrentSearchIndex < 0 || CurrentSearchIndex >= _searchResults.Count)
                return;

            var result = _searchResults[CurrentSearchIndex];
            var range = result.ToRange();
            _document.ReplaceText(range, _searchOptions.ReplaceText);

            // 重新搜索
            Search();
        }

        /// <summary>替换全部。</summary>
        public int ReplaceAll()
        {
            if (_document == null || string.IsNullOrEmpty(_searchOptions.SearchText))
                return 0;

            int count = 0;
            var pattern = BuildSearchPattern();
            var regex = new Regex(pattern, _searchOptions.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

            var lines = _document.TextLines.ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                lines[i] = regex.Replace(lines[i], m =>
                {
                    count++;
                    return _searchOptions.ReplaceText;
                });
            }

            if (count > 0)
            {
                _document.ApplyEdit(0, _document.Text.Length, string.Join("\n", lines));
                InvalidateHighlighting();
            }

            Search();
            return count;
        }

        private string BuildSearchPattern()
        {
            if (_searchOptions.UseRegex)
                return _searchOptions.SearchText;

            string escaped = Regex.Escape(_searchOptions.SearchText);
            if (_searchOptions.WholeWord)
                return @"\b" + escaped + @"\b";
            return escaped;
        }

        private void NavigateToSearchResult(int index)
        {
            if (index < 0 || index >= _searchResults.Count) return;
            var result = _searchResults[index];
            var range = result.ToRange();
            _document!.Cursor = range.End;
            _document.Selection = range;
            ScrollToCenter(result.Line);
            OnPropertyChanged(nameof(Cursor));
            OnPropertyChanged(nameof(Selection));
        }

        // ── 格式化 ────────────────────────────────────────────────────────────────

        /// <summary>格式化当前文档。</summary>
        public void FormatDocument(FormatterOptions? options = null)
        {
            if (_document == null || string.IsNullOrEmpty(_document.Text)) return;

            var oldText = _document.Text;
            var formatted = CSharpFormatter.Format(oldText, options);
            if (formatted == oldText) return;

            var cursorLine = _document.Cursor.Line;
            var cursorCol = _document.Cursor.Column;

            _document.ApplyEdit(0, oldText.Length, formatted);
            _viewport.TotalLines = _document.LineCount;

            // 尝试恢复光标位置
            int newLine = Math.Min(cursorLine, _document.LineCount - 1);
            int newCol = Math.Min(cursorCol, _document.TextLines[newLine].Length);
            _document.Cursor = new TextPosition(newLine, newCol);
            _document.Selection = null;

            UpdateFoldRanges();
            InvalidateHighlighting();
            EnsureCursorVisible();
        }

        // ── 渲染数据 ──────────────────────────────────────────────────────────────

        private List<ColoredSegment>? _cachedSegments;
        private int _cachedSegmentLine = -1;

        /// <summary>使高亮缓存失效。</summary>
        public void InvalidateHighlighting()
        {
            _cachedSegments = null;
            _cachedSegmentLine = -1;
        }

        /// <summary>获取可见行的渲染信息。</summary>
        public IReadOnlyList<VisibleLineInfo> GetVisibleLines()
        {
            var result = new List<VisibleLineInfo>();
            if (_document == null) return result;

            // 预计算全文着色 (对于大文件应改为按视口范围计算)
            var allSegments = SyntaxHighlighter.Highlight(_document.Text, _language);
            var lineStarts = SyntaxHighlighter.ComputeLineStarts(_document.Text);

            int firstLine = _viewport.FirstVisibleLine;
            int lastLine = _viewport.LastVisibleLineExclusive;
            int cursorLine = _document.Cursor.Line;

            for (int i = firstLine; i < lastLine && i < _document.LineCount; i++)
            {
                if (IsLineFolded(i + 1)) continue;

                var lineText = _document.TextLines[i];
                var lineSegments = GetSegmentsForLine(allSegments, lineStarts, i);

                result.Add(new VisibleLineInfo
                {
                    LineNumber = i + 1,
                    Text = lineText,
                    Segments = lineSegments,
                    HasBreakpoint = _breakpointLines.Contains(i + 1),
                    HasError = _errorMarks.ContainsKey(i + 1),
                    Errors = GetErrorMarks(i + 1),
                    IsCurrentLine = i == cursorLine,
                    IsFolded = false,
                    Indent = _document.GetLineIndent(i)
                });
            }

            return result;
        }

        private static IReadOnlyList<ColoredSegment> GetSegmentsForLine(
            IReadOnlyList<ColoredSegment> allSegments, List<int> lineStarts, int lineIndex)
        {
            var result = new List<ColoredSegment>();
            int lineStart = lineIndex < lineStarts.Count ? lineStarts[lineIndex] : 0;
            int lineEnd = lineIndex + 1 < lineStarts.Count ? lineStarts[lineIndex + 1] : int.MaxValue;

            foreach (var seg in allSegments)
            {
                int segStart = seg.Segment.Start;
                int segEnd = seg.Segment.Start + seg.Segment.Length;
                if (segStart < lineEnd && segEnd > lineStart)
                    result.Add(seg);
            }
            return result;
        }

        // ── 辅助 ──────────────────────────────────────────────────────────────────

        private TextPosition ClampPosition(TextPosition pos)
        {
            if (_document == null) return pos;
            int line = Math.Max(0, Math.Min(pos.Line, _document.LineCount - 1));
            int col = Math.Max(0, Math.Min(pos.Column, _document.TextLines[line].Length));
            return new TextPosition(line, col);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CodeEditorViewModel — MVVM ViewModel
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 代码编辑器 ViewModel。提供 MVVM 数据绑定支持,
    /// 管理 opened documents、补全列表、签名提示、错误标记、断点标记等。
    /// </summary>
    public sealed class CodeEditorViewModel : INotifyPropertyChanged, IDisposable
    {
        // ── 字段 ──────────────────────────────────────────────────────────────────

        private readonly TextEditorControl _editor = new();
        private readonly ObservableCollection<CodeDocument> _openedDocuments = new();
        private readonly ObservableCollection<ErrorMark> _errorMarks = new();
        private readonly ObservableCollection<int> _breakpointMarks = new();
        private readonly ObservableCollection<CompletionItem> _activeCompletionList = new();
        private SignatureHelp? _activeSignatureHelp;
        private QuickInfo? _activeQuickInfo;
        private CodeDocument? _currentDocument;
        private bool _isModified;
        private bool _isSearching;
        private bool _isReplacing;
        private string _statusText = "就绪";
        private IntelliSenseProvider? _intelliSense;
        private DebugSession? _debugSession;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>编辑器控件。</summary>
        public TextEditorControl Editor => _editor;

        /// <summary>已打开的文档列表。</summary>
        public ObservableCollection<CodeDocument> OpenedDocuments => _openedDocuments;

        /// <summary>当前活动文档。</summary>
        public CodeDocument? CurrentDocument
        {
            get => _currentDocument;
            set
            {
                if (_currentDocument != value)
                {
                    _currentDocument = value;
                    _editor.Document = value;
                    IsModified = value?.Modified ?? false;
                    UpdateErrorMarks();
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>活动补全列表。</summary>
        public ObservableCollection<CompletionItem> ActiveCompletionList => _activeCompletionList;

        /// <summary>活动签名提示。</summary>
        public SignatureHelp? ActiveSignatureHelp
        {
            get => _activeSignatureHelp;
            set { if (_activeSignatureHelp != value) { _activeSignatureHelp = value; OnPropertyChanged(); } }
        }

        /// <summary>活动悬停提示。</summary>
        public QuickInfo? ActiveQuickInfo
        {
            get => _activeQuickInfo;
            set { if (_activeQuickInfo != value) { _activeQuickInfo = value; OnPropertyChanged(); } }
        }

        /// <summary>错误标记列表。</summary>
        public ObservableCollection<ErrorMark> ErrorMarks => _errorMarks;

        /// <summary>断点标记列表 (1 基行号)。</summary>
        public ObservableCollection<int> BreakpointMarks => _breakpointMarks;

        /// <summary>文档是否已修改。</summary>
        public bool IsModified
        {
            get => _isModified;
            set { if (_isModified != value) { _isModified = value; OnPropertyChanged(); } }
        }

        /// <summary>是否处于搜索模式。</summary>
        public bool IsSearching
        {
            get => _isSearching;
            set { if (_isSearching != value) { _isSearching = value; OnPropertyChanged(); } }
        }

        /// <summary>是否处于替换模式。</summary>
        public bool IsReplacing
        {
            get => _isReplacing;
            set { if (_isReplacing != value) { _isReplacing = value; OnPropertyChanged(); } }
        }

        /// <summary>状态栏文本。</summary>
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        /// <summary>IntelliSense 提供者。</summary>
        public IntelliSenseProvider? IntelliSense
        {
            get => _intelliSense;
            set { _intelliSense = value; }
        }

        /// <summary>调试会话。</summary>
        public DebugSession? DebugSession
        {
            get => _debugSession;
            set { _debugSession = value; }
        }

        /// <summary>搜索选项 (转发到 Editor)。</summary>
        public SearchOptions SearchOptions => _editor.SearchOptions;

        /// <summary>搜索结果数量。</summary>
        public int SearchResultCount => _editor.SearchResultCount;

        /// <summary>当前搜索结果索引。</summary>
        public int CurrentSearchIndex => _editor.CurrentSearchIndex;

        // ── 命令 ──────────────────────────────────────────────────────────────────

        /// <summary>保存命令。</summary>
        public ICommand SaveCommand { get; }

        /// <summary>格式化命令。</summary>
        public ICommand FormatCommand { get; }

        /// <summary>撤销命令。</summary>
        public ICommand UndoCommand { get; }

        /// <summary>重做命令。</summary>
        public ICommand RedoCommand { get; }

        /// <summary>搜索命令。</summary>
        public ICommand SearchCommand { get; }

        /// <summary>查找下一个命令。</summary>
        public ICommand FindNextCommand { get; }

        /// <summary>查找上一个命令。</summary>
        public ICommand FindPreviousCommand { get; }

        /// <summary>替换当前命令。</summary>
        public ICommand ReplaceCurrentCommand { get; }

        /// <summary>全部替换命令。</summary>
        public ICommand ReplaceAllCommand { get; }

        /// <summary>关闭搜索面板命令。</summary>
        public ICommand CloseSearchCommand { get; }

        /// <summary>切换断点命令。</summary>
        public ICommand ToggleBreakpointCommand { get; }

        /// <summary>全选命令。</summary>
        public ICommand SelectAllCommand { get; }

        // ── 构造 ──────────────────────────────────────────────────────────────────

        public CodeEditorViewModel()
        {
            SaveCommand = new RelayCommand(() => Save(), () => _currentDocument != null);
            FormatCommand = new RelayCommand(() => FormatDocument(), () => _currentDocument != null);
            UndoCommand = new RelayCommand(() => { _editor.Undo(); UpdateModifiedState(); },
                () => _editor.CanUndo);
            RedoCommand = new RelayCommand(() => { _editor.Redo(); UpdateModifiedState(); },
                () => _editor.CanRedo);
            SearchCommand = new RelayCommand(() =>
            {
                _editor.Search();
                OnPropertyChanged(nameof(SearchResultCount));
                OnPropertyChanged(nameof(CurrentSearchIndex));
                StatusText = _editor.SearchResultCount > 0
                    ? $"找到 {_editor.SearchResultCount} 个结果"
                    : "未找到匹配项";
            });
            FindNextCommand = new RelayCommand(() =>
            {
                _editor.FindNext();
                OnPropertyChanged(nameof(CurrentSearchIndex));
            });
            FindPreviousCommand = new RelayCommand(() =>
            {
                _editor.FindPrevious();
                OnPropertyChanged(nameof(CurrentSearchIndex));
            });
            ReplaceCurrentCommand = new RelayCommand(() =>
            {
                _editor.ReplaceCurrent();
                UpdateModifiedState();
                OnPropertyChanged(nameof(SearchResultCount));
            });
            ReplaceAllCommand = new RelayCommand(() =>
            {
                int count = _editor.ReplaceAll();
                UpdateModifiedState();
                StatusText = $"已替换 {count} 处";
                OnPropertyChanged(nameof(SearchResultCount));
            });
            CloseSearchCommand = new RelayCommand(() =>
            {
                IsSearching = false;
                IsReplacing = false;
            });
            ToggleBreakpointCommand = new RelayCommand(param =>
            {
                int line = _editor.Cursor.Line + 1;
                _editor.ToggleBreakpoint(line);
                SyncBreakpointMarks();
            });
            SelectAllCommand = new RelayCommand(() => _editor.SelectAll());

            // 订阅编辑器事件
            _editor.TextChanged += OnEditorTextChanged;
            _editor.BreakpointToggled += OnBreakpointToggled;
        }

        // ── 文档管理 ──────────────────────────────────────────────────────────────

        /// <summary>打开文档。</summary>
        public void OpenDocument(CodeDocument document)
        {
            if (document == null) return;
            if (!_openedDocuments.Contains(document))
                _openedDocuments.Add(document);
            CurrentDocument = document;
            StatusText = $"已打开: {document.FilePath}";
        }

        /// <summary>从文件打开文档。</summary>
        public void OpenFile(string filePath)
        {
            var doc = new CodeDocument(filePath,
                System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : string.Empty);
            OpenDocument(doc);
        }

        /// <summary>关闭文档。</summary>
        public void CloseDocument(CodeDocument document)
        {
            if (document == null) return;
            _openedDocuments.Remove(document);
            if (_currentDocument == document)
            {
                CurrentDocument = _openedDocuments.Count > 0 ? _openedDocuments[^1] : null;
            }
        }

        /// <summary>保存当前文档。</summary>
        public bool Save()
        {
            if (_currentDocument == null) return false;
            _editor.Save();
            IsModified = false;
            StatusText = $"已保存: {_currentDocument.FilePath}";
            return true;
        }

        // ── IntelliSense ──────────────────────────────────────────────────────────

        /// <summary>请求补全列表。</summary>
        public void RequestCompletion()
        {
            _activeCompletionList.Clear();
            if (_intelliSense == null || _currentDocument == null) return;

            var pos = _editor.Cursor;
            int offset = _currentDocument.PositionToOffset(pos);
            var items = _intelliSense.GetCompletionItems(_currentDocument, offset);
            foreach (var item in items)
                _activeCompletionList.Add(item);

            StatusText = _activeCompletionList.Count > 0
                ? $"补全: {_activeCompletionList.Count} 项"
                : "无补全项";
        }

        /// <summary>请求签名提示。</summary>
        public void RequestSignatureHelp()
        {
            if (_intelliSense == null || _currentDocument == null)
            {
                ActiveSignatureHelp = null;
                return;
            }

            var pos = _editor.Cursor;
            int offset = _currentDocument.PositionToOffset(pos);
            ActiveSignatureHelp = _intelliSense.GetSignatureHelp(_currentDocument, offset);
        }

        /// <summary>请求悬停提示。</summary>
        public void RequestQuickInfo(TextPosition position)
        {
            if (_intelliSense == null || _currentDocument == null)
            {
                ActiveQuickInfo = null;
                return;
            }

            int offset = _currentDocument.PositionToOffset(position);
            ActiveQuickInfo = _intelliSense.GetQuickInfo(_currentDocument, offset);
        }

        /// <summary>提交补全项 (插入到文档)。</summary>
        public void CommitCompletion(CompletionItem item)
        {
            if (item == null || _currentDocument == null) return;

            // 删除已输入的过滤文本
            var cursor = _editor.Cursor;
            var filterText = item.FilterText ?? string.Empty;
            if (!string.IsNullOrEmpty(filterText) && cursor.Column >= filterText.Length)
            {
                var line = _currentDocument.TextLines[cursor.Line];
                var prefix = line.Substring(cursor.Column - filterText.Length, filterText.Length);
                if (prefix.Equals(filterText, StringComparison.OrdinalIgnoreCase))
                {
                    var deleteRange = new TextRange(
                        new TextPosition(cursor.Line, cursor.Column - filterText.Length),
                        cursor);
                    _currentDocument.ReplaceText(deleteRange, string.Empty);
                }
            }

            _editor.TypeText(item.InsertText ?? item.DisplayText);
            _activeCompletionList.Clear();
            UpdateModifiedState();
        }

        /// <summary>关闭补全列表。</summary>
        public void DismissCompletion()
        {
            _activeCompletionList.Clear();
        }

        /// <summary>关闭签名提示。</summary>
        public void DismissSignatureHelp()
        {
            ActiveSignatureHelp = null;
        }

        // ── 诊断 ──────────────────────────────────────────────────────────────────

        /// <summary>刷新错误标记 (从 IntelliSense 诊断)。</summary>
        public void RefreshDiagnostics()
        {
            if (_intelliSense == null || _currentDocument == null)
            {
                _errorMarks.Clear();
                _editor.ClearErrorMarks();
                return;
            }

            var diagnostics = _intelliSense.GetDiagnostics(_currentDocument);
            _errorMarks.Clear();
            _editor.ClearErrorMarks();

            var newMarks = new List<ErrorMark>();
            foreach (var diag in diagnostics)
            {
                var mark = new ErrorMark(
                    diag.Span.Start.Line + 1,
                    diag.Message,
                    diag.Severity == CodeDiagnosticSeverity.Error
                        ? ErrorSeverity.Error
                        : diag.Severity == CodeDiagnosticSeverity.Warning
                            ? ErrorSeverity.Warning
                            : ErrorSeverity.Info);
                newMarks.Add(mark);
                _errorMarks.Add(mark);
                _editor.AddErrorMark(mark);
            }

            StatusText = newMarks.Count > 0
                ? $"{newMarks.Count} 个诊断"
                : "无错误";
        }

        private void UpdateErrorMarks()
        {
            _errorMarks.Clear();
            if (_currentDocument == null) return;
            foreach (var mark in _editor.GetAllErrorMarks())
                _errorMarks.Add(mark);
        }

        // ── 格式化 ────────────────────────────────────────────────────────────────

        /// <summary>格式化当前文档。</summary>
        public void FormatDocument(FormatterOptions? options = null)
        {
            _editor.FormatDocument(options);
            UpdateModifiedState();
            StatusText = "格式化完成";
        }

        // ── 调试 ──────────────────────────────────────────────────────────────────

        /// <summary>开始调试。</summary>
        public void StartDebug(ScriptRuntimeManager runtime)
        {
            _debugSession = new DebugSession();
            _debugSession.Attach(runtime);
            SyncBreakpointMarks();
            StatusText = "调试已启动";
        }

        /// <summary>停止调试。</summary>
        public void StopDebug()
        {
            _debugSession?.Stop();
            _debugSession = null;
            StatusText = "调试已停止";
        }

        /// <summary>获取调用栈。</summary>
        public IReadOnlyList<StackFrame> GetCallStack()
            => _debugSession?.GetCallStack() ?? Array.Empty<StackFrame>();

        /// <summary>获取局部变量。</summary>
        public IReadOnlyList<VariableInfo> GetLocalVariables()
            => _debugSession?.GetLocalVariables() ?? Array.Empty<VariableInfo>();

        // ── 内部同步 ──────────────────────────────────────────────────────────────

        private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
        {
            UpdateModifiedState();
        }

        private void OnBreakpointToggled(object? sender, BreakpointToggleEventArgs e)
        {
            SyncBreakpointMarks();
        }

        private void SyncBreakpointMarks()
        {
            _breakpointMarks.Clear();
            foreach (var line in _editor.BreakpointLines)
                _breakpointMarks.Add(line);
        }

        private void UpdateModifiedState()
        {
            IsModified = _currentDocument?.Modified ?? false;
        }

        // ── INotifyPropertyChanged ───────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            _editor.TextChanged -= OnEditorTextChanged;
            _editor.BreakpointToggled -= OnBreakpointToggled;
            _intelliSense?.Dispose();
        }
    }
}
