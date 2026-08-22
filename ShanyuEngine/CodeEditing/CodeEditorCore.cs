// ==================================================================================================
//  CodeEditorCore.cs — 山雨引擎原生代码编辑器核心
//
//  命名空间: ShanyuEngine.CodeEditing
//  依赖:     System, System.Collections.Generic, System.IO
//
//  功能:
//    1. CodeDocument        — 按行存储的代码文档 (光标/选择/撤销重做/插入删除替换)
//    2. SyntaxTokenizer     — 词法分析器基类 + C#/HLSL 具体实现
//    3. SyntaxHighlighter   — 应用 Token 到文本着色, 输出 (TextSegment, Color) 列表
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace ShanyuEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  基础结构
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Token 类型 (用于着色分类)。</summary>
    public enum TokenType
    {
        Keyword, Type, String, Comment, Number, Operator,
        MethodName, Identifier, Whitespace, Punctuation, Preprocessor, Unknown
    }

    /// <summary>词法 Token: 类型 + 文本 + 位置 (Line/Column 均为 0 基) + 长度。</summary>
    public readonly record struct Token(TokenType Type, string Text, int Line, int Column, int Length);

    /// <summary>文本片段 (基于行/列)。</summary>
    public readonly record struct TextSegment(string Text, int StartLine, int StartCol);

    /// <summary>支持的语言。</summary>
    public enum CodeLanguage { CSharp, Hlsl, Plain }

    /// <summary>文本位置 (Line/Column 均为 0 基)。</summary>
    public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
    {
        public static readonly TextPosition Zero = new(0, 0);
        public int CompareTo(TextPosition other)
        {
            int c = Line.CompareTo(other.Line);
            return c != 0 ? c : Column.CompareTo(other.Column);
        }
        public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
        public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
        public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
        public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
    }

    /// <summary>文本范围 (半开区间 [Start, End))。</summary>
    public readonly record struct TextRange(TextPosition Start, TextPosition End)
    {
        public static TextRange Empty(TextPosition p) => new(p, p);
        public bool IsEmpty => Start.Equals(End);
        public TextRange Normalized => Start <= End ? this : new TextRange(End, Start);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CodeDocument — 代码文档
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>代码文档: 按行存储文本, 维护光标/选择/撤销重做栈与修改标记。</summary>
    public sealed class CodeDocument
    {
        public class Snapshot
        {
            public List<string> Lines { get; }
            public TextPosition Cursor { get; }
            public TextRange? Selection { get; }
            public bool Modified { get; }
            public Snapshot(List<string> lines, TextPosition cursor, TextRange? sel, bool modified)
            {
                Lines = lines; Cursor = cursor; Selection = sel; Modified = modified;
            }
        }

        private List<string> _lines;

        public string FilePath { get; }
        public IReadOnlyList<string> TextLines => _lines;
        public TextPosition Cursor { get; set; } = TextPosition.Zero;
        public TextRange? Selection { get; set; }
        public Stack<Snapshot> UndoStack { get; } = new();
        public Stack<Snapshot> RedoStack { get; } = new();
        public bool Modified { get; private set; }
        public int LineCount => _lines.Count;
        public string Text => string.Join("\n", _lines);
        public CodeLanguage Language { get; set; } = CodeLanguage.CSharp;

        public CodeDocument(string filePath, string text)
        {
            FilePath = filePath ?? string.Empty;
            _lines = SplitLines(text ?? string.Empty);
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string> { string.Empty };
            var norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return new List<string>(norm.Split('\n'));
        }

        public void InsertText(TextPosition pos, string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            PushSnapshot();
            InsertCore(pos, text);
            Modified = true;
        }

        public void DeleteText(TextRange range)
        {
            range = range.Normalized;
            if (range.IsEmpty) return;
            PushSnapshot();
            DeleteCore(range);
            Cursor = range.Start;
            Modified = true;
        }

        public void ReplaceText(TextRange range, string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            range = range.Normalized;
            PushSnapshot();
            if (!range.IsEmpty) DeleteCore(range);
            InsertCore(range.Start, text);
            Modified = true;
        }

        private void InsertCore(TextPosition pos, string text)
        {
            var norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = norm.Split('\n');
            int line = Math.Clamp(pos.Line, 0, _lines.Count > 0 ? _lines.Count - 1 : 0);
            if (line >= _lines.Count) { while (_lines.Count <= line) _lines.Add(string.Empty); }
            string cur = _lines[line];
            int col = Math.Clamp(pos.Column, 0, cur.Length);
            if (parts.Length == 1)
            {
                _lines[line] = cur.Insert(col, parts[0]);
                Cursor = new TextPosition(line, col + parts[0].Length);
            }
            else
            {
                string before = cur.Substring(0, col);
                string after = cur.Substring(col);
                _lines[line] = before + parts[0];
                for (int i = 1; i < parts.Length - 1; i++)
                    _lines.Insert(line + i, parts[i]);
                _lines.Insert(line + parts.Length - 1, parts[^1] + after);
                Cursor = new TextPosition(line + parts.Length - 1, parts[^1].Length);
            }
            Selection = null;
        }

        private void DeleteCore(TextRange range)
        {
            int sL = Math.Max(0, range.Start.Line);
            int eL = Math.Min(range.End.Line, _lines.Count - 1);
            if (sL >= _lines.Count) return;
            string sLine = _lines[sL];
            string eLine = _lines[eL];
            int sC = Math.Clamp(range.Start.Column, 0, sLine.Length);
            int eC = Math.Clamp(range.End.Column, 0, eLine.Length);
            _lines[sL] = sLine.Substring(0, sC) + eLine.Substring(eC);
            for (int i = eL; i > sL; i--) _lines.RemoveAt(i);
            Selection = null;
        }

        private void PushSnapshot()
        {
            UndoStack.Push(new Snapshot(new List<string>(_lines), Cursor, Selection, Modified));
            RedoStack.Clear();
        }

        public bool Undo()
        {
            if (UndoStack.Count == 0) return false;
            RedoStack.Push(new Snapshot(new List<string>(_lines), Cursor, Selection, Modified));
            var s = UndoStack.Pop();
            _lines = s.Lines; Cursor = s.Cursor; Selection = s.Selection; Modified = s.Modified;
            return true;
        }

        public bool Redo()
        {
            if (RedoStack.Count == 0) return false;
            UndoStack.Push(new Snapshot(new List<string>(_lines), Cursor, Selection, Modified));
            var s = RedoStack.Pop();
            _lines = s.Lines; Cursor = s.Cursor; Selection = s.Selection; Modified = s.Modified;
            return true;
        }

        public void MarkSaved() => Modified = false;

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, Text, Encoding.UTF8);
            Modified = false;
        }

        public int PositionToOffset(TextPosition pos)
        {
            int off = 0;
            int line = Math.Clamp(pos.Line, 0, Math.Max(0, _lines.Count - 1));
            for (int i = 0; i < line; i++) off += _lines[i].Length + 1;
            if (line < _lines.Count) off += Math.Clamp(pos.Column, 0, _lines[line].Length);
            return off;
        }

        public TextPosition OffsetToPosition(int offset)
        {
            if (offset <= 0) return TextPosition.Zero;
            int rem = offset, line = 0;
            while (line < _lines.Count && rem > _lines[line].Length)
            {
                rem -= _lines[line].Length + 1;
                line++;
                if (rem < 0) { rem = 0; break; }
            }
            return new TextPosition(line, Math.Max(0, rem));
        }

        public string GetText(TextRange range)
        {
            range = range.Normalized;
            if (range.IsEmpty) return string.Empty;
            int s = PositionToOffset(range.Start);
            int e = PositionToOffset(range.End);
            return Text.Substring(s, Math.Max(0, e - s));
        }

        public string GetLineText(int line) => (line >= 0 && line < _lines.Count) ? _lines[line] : string.Empty;

        public int GetBraceDepth(int line)
        {
            int d = 0;
            for (int i = 0; i <= line && i < _lines.Count; i++)
                foreach (var c in _lines[i]) { if (c == '{') d++; else if (c == '}') d--; }
            return Math.Max(0, d);
        }

        public CodeDocument Clone() => new CodeDocument(FilePath, Text) { Cursor = Cursor, Selection = Selection, Language = Language };
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  SyntaxTokenizer — 词法分析器基类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>词法分析器基类。子类通过 IsKeyword/IsKnownType 提供语言词表。</summary>
    public abstract class SyntaxTokenizer
    {
        public abstract IReadOnlyList<Token> Tokenize(string source);
        protected abstract bool IsKeyword(string word);
        protected abstract bool IsKnownType(string word);

        public static SyntaxTokenizer Create(CodeLanguage lang) => lang switch
        {
            CodeLanguage.CSharp => new CSharpTokenizer(),
            CodeLanguage.Hlsl => new HlslTokenizer(),
            _ => new PlainTokenizer(),
        };

        protected List<Token> ScanCore(string src)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrEmpty(src)) return tokens;
            int i = 0, line = 0, col = 0, len = src.Length;

            while (i < len)
            {
                char c = src[i];

                // 换行
                if (c == '\r' || c == '\n')
                {
                    int s = i, sl = line, sc = col;
                    while (i < len && (src[i] == '\r' || src[i] == '\n'))
                    {
                        if (src[i] == '\n') { line++; col = 0; }
                        else if (src[i] == '\r' && (i + 1 >= len || src[i + 1] != '\n')) { line++; col = 0; }
                        i++;
                    }
                    tokens.Add(new Token(TokenType.Whitespace, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 空白
                if (char.IsWhiteSpace(c))
                {
                    int s = i, sl = line, sc = col;
                    while (i < len && char.IsWhiteSpace(src[i]) && src[i] != '\r' && src[i] != '\n') { col++; i++; }
                    tokens.Add(new Token(TokenType.Whitespace, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 行注释
                if (c == '/' && i + 1 < len && src[i + 1] == '/')
                {
                    int s = i, sl = line, sc = col;
                    while (i < len && src[i] != '\n' && src[i] != '\r') { col++; i++; }
                    tokens.Add(new Token(TokenType.Comment, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 块注释 (逐字符解析)
                if (c == '/' && i + 1 < len && src[i + 1] == '*')
                {
                    int s = i, sl = line, sc = col;
                    i += 2; col += 2;
                    while (i + 1 < len && !(src[i] == '*' && src[i + 1] == '/'))
                    {
                        if (src[i] == '\n') { line++; col = 0; }
                        else if (src[i] == '\r') { if (i + 1 < len && src[i + 1] == '\n') col = 0; else { line++; col = 0; } }
                        else col++;
                        i++;
                    }
                    if (i + 1 < len) { i += 2; col += 2; }
                    tokens.Add(new Token(TokenType.Comment, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 预处理
                if (c == '#' && (i == 0 || src[i - 1] == '\n'))
                {
                    int s = i, sl = line, sc = col;
                    while (i < len && src[i] != '\n' && src[i] != '\r') { col++; i++; }
                    tokens.Add(new Token(TokenType.Preprocessor, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 字符串 (含 $" /@" / $&quot;@" )
                if (TryScanString(src, ref i, ref line, ref col, out var strTok))
                {
                    tokens.Add(strTok);
                    continue;
                }

                // 字符字面量
                if (c == '\'')
                {
                    int s = i, sl = line, sc = col;
                    i++; col++;
                    while (i < len && src[i] != '\'')
                    {
                        if (src[i] == '\\' && i + 1 < len) { i += 2; col += 2; }
                        else { i++; col++; }
                    }
                    if (i < len) { i++; col++; }
                    tokens.Add(new Token(TokenType.String, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 数字
                if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(src[i + 1])))
                {
                    int s = i, sl = line, sc = col;
                    if (c == '0' && i + 1 < len && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                    {
                        i += 2; col += 2;
                        while (i < len && IsHex(src[i])) { i++; col++; }
                    }
                    else
                    {
                        while (i < len && (char.IsDigit(src[i]) || src[i] == '.')) { i++; col++; }
                        while (i < len && "fFdDmMuUlLeE+-".Contains(src[i]))
                        {
                            char n = src[i];
                            if (n == 'e' || n == 'E') { i++; col++; if (i < len && (src[i] == '+' || src[i] == '-')) { i++; col++; } continue; }
                            if (n == '+' || n == '-') break;
                            i++; col++;
                        }
                    }
                    tokens.Add(new Token(TokenType.Number, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 标识符
                if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    int s = i, sl = line, sc = col;
                    if (c == '@') { i++; col++; }
                    while (i < len && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) { i++; col++; }
                    string word = src.Substring(s, i - s);
                    var kind = ClassifyIdentifier(word, src, i);
                    tokens.Add(new Token(kind, word, sl, sc, i - s));
                    continue;
                }

                // 运算符
                if ("+-*/%=<>!&|^~?".Contains(c))
                {
                    int s = i, sl = line, sc = col;
                    while (i < len && "+-*/%=<>!&|^~?".Contains(src[i])) { i++; col++; }
                    tokens.Add(new Token(TokenType.Operator, src.Substring(s, i - s), sl, sc, i - s));
                    continue;
                }

                // 标点
                tokens.Add(new Token(TokenType.Punctuation, c.ToString(), line, col, 1));
                i++; col++;
            }
            return tokens;
        }

        protected TokenType ClassifyIdentifier(string word, string src, int next)
        {
            if (IsKeyword(word)) return TokenType.Keyword;
            int p = next;
            while (p < src.Length && char.IsWhiteSpace(src[p]) && src[p] != '\r' && src[p] != '\n') p++;
            bool followedByParen = p < src.Length && src[p] == '(';
            if (followedByParen) return TokenType.MethodName;
            if (IsKnownType(word)) return TokenType.Type;
            if (word.Length > 0 && char.IsUpper(word[0])) return TokenType.Type;
            return TokenType.Identifier;
        }

        private static bool IsHex(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private bool TryScanString(string src, ref int i, ref int line, ref int col, out Token token)
        {
            token = default;
            int s = i, sl = line, sc = col, len = src.Length;
            char c = src[i];
            bool interp = false, verb = false;
            if (c == '$' && i + 1 < len && src[i + 1] == '"') { interp = true; i += 2; col += 2; }
            else if (c == '$' && i + 2 < len && src[i + 1] == '@' && src[i + 2] == '"') { interp = true; verb = true; i += 3; col += 3; }
            else if (c == '@' && i + 1 < len && src[i + 1] == '"') { verb = true; i += 2; col += 2; }
            else if (c == '@' && i + 2 < len && src[i + 1] == '$' && src[i + 2] == '"') { verb = true; interp = true; i += 3; col += 3; }
            else if (c == '"') { i++; col++; }
            else return false;

            if (verb)
            {
                while (i < len)
                {
                    if (src[i] == '"')
                    {
                        if (i + 1 < len && src[i + 1] == '"') { i += 2; col += 2; continue; }
                        i++; col++; break;
                    }
                    if (src[i] == '\n') { line++; col = 0; }
                    else if (src[i] == '\r') { if (i + 1 < len && src[i + 1] == '\n') col = 0; else { line++; col = 0; } }
                    else col++;
                    i++;
                }
            }
            else
            {
                int brace = 0;
                while (i < len)
                {
                    char ch = src[i];
                    if (ch == '\\' && i + 1 < len) { i += 2; col += 2; continue; }
                    if (interp && ch == '{') { brace++; i++; col++; continue; }
                    if (interp && ch == '}' && brace > 0) { brace--; i++; col++; continue; }
                    if (ch == '"' && brace == 0) { i++; col++; break; }
                    if (ch == '\n') { line++; col = 0; }
                    else if (ch == '\r') { if (i + 1 < len && src[i + 1] == '\n') col = 0; else { line++; col = 0; } }
                    else col++;
                    i++;
                }
            }
            token = new Token(TokenType.String, src.Substring(s, i - s), sl, sc, i - s);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CSharpTokenizer — C# 词法分析器
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class CSharpTokenizer : SyntaxTokenizer
    {
        private static readonly HashSet<string> Keywords = new()
        {
            "abstract","as","async","await","base","bool","break","byte","case","catch","char",
            "checked","class","const","continue","decimal","default","delegate","do","double",
            "else","enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","get","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params","partial",
            "private","protected","public","readonly","ref","return","sbyte","sealed","set","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try",
            "typeof","uint","ulong","unchecked","unsafe","ushort","using","value","var","virtual",
            "void","volatile","while","yield","nameof","when","where","record","init","required","file"
        };

        private static readonly HashSet<string> KnownTypes = new()
        {
            "Vector2","Vector3","Vector4","Quaternion","Matrix4x4","Transform","Entity",
            "List","Dictionary","HashSet","Queue","Stack","Array","Span","ReadOnlySpan",
            "Task","Action","Func","Tuple","ValueTuple","Color","StringBuilder",
            "ActorComponent","GameObject","Component","IEnumerable","IDisposable"
        };

        protected override bool IsKeyword(string word) => Keywords.Contains(word);
        protected override bool IsKnownType(string word) => KnownTypes.Contains(word);
        public override IReadOnlyList<Token> Tokenize(string source) => ScanCore(source ?? string.Empty);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  HlslTokenizer — HLSL 词法分析器
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class HlslTokenizer : SyntaxTokenizer
    {
        private static readonly HashSet<string> Keywords = new()
        {
            "void","return","if","else","for","while","do","break","continue","const","static",
            "struct","typedef","namespace","using","sampler","register","true","false",
            "discard","in","out","inout","uniform","row_major","column_major","cbuffer","tbuffer",
            "groupshared","extern","volatile","matrix","vector","technique","pass"
        };

        private static readonly HashSet<string> KnownTypes = new()
        {
            "float","float2","float3","float4","half","half2","half3","half4",
            "int","int2","int3","int4","uint","uint2","uint3","uint4",
            "bool","bool2","bool3","bool4","double","double2","double3","double4",
            "float2x2","float3x3","float4x4","Texture2D","Texture3D","TextureCube",
            "Texture2DArray","RWTexture2D","RWTexture3D","RWStructuredBuffer","StructuredBuffer",
            "ByteAddressBuffer","RWByteAddressBuffer","SamplerState","SamplerComparisonState",
            "ConstantBuffer","InputPatch","OutputPatch","SV_Position","SV_POSITION","SV_Target",
            "SV_TARGET","SV_Depth","TEXCOORD","NORMAL","COLOR","TANGENT","BINORMAL"
        };

        protected override bool IsKeyword(string word) => Keywords.Contains(word);
        protected override bool IsKnownType(string word) => KnownTypes.Contains(word);
        public override IReadOnlyList<Token> Tokenize(string source) => ScanCore(source ?? string.Empty);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  PlainTokenizer — 纯文本
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class PlainTokenizer : SyntaxTokenizer
    {
        protected override bool IsKeyword(string word) => false;
        protected override bool IsKnownType(string word) => false;
        public override IReadOnlyList<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrEmpty(source)) return tokens;
            int line = 0, col = 0;
            foreach (var ch in source)
            {
                if (ch == '\n') { tokens.Add(new Token(TokenType.Whitespace, "\n", line, col, 1)); line++; col = 0; }
                else if (ch == '\r') { }
                else { tokens.Add(new Token(TokenType.Identifier, ch.ToString(), line, col, 1)); col++; }
            }
            return tokens;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  SyntaxHighlighter — 语法高亮器
    // ════════════════════════════════════════════════════════════════════════════

    public static class SyntaxHighlighter
    {
        private static readonly Dictionary<TokenType, Color> TokenColors = new()
        {
            [TokenType.Keyword] = Color.FromArgb(86, 156, 214),
            [TokenType.Type] = Color.FromArgb(78, 201, 176),
            [TokenType.String] = Color.FromArgb(214, 157, 133),
            [TokenType.Comment] = Color.FromArgb(106, 153, 85),
            [TokenType.Number] = Color.FromArgb(181, 129, 255),
            [TokenType.Operator] = Color.FromArgb(180, 180, 180),
            [TokenType.MethodName] = Color.FromArgb(220, 220, 130),
            [TokenType.Identifier] = Color.FromArgb(212, 212, 212),
            [TokenType.Punctuation] = Color.FromArgb(180, 180, 180),
            [TokenType.Preprocessor] = Color.FromArgb(155, 155, 155),
            [TokenType.Whitespace] = Color.FromArgb(212, 212, 212),
            [TokenType.Unknown] = Color.FromArgb(212, 212, 212),
        };

        public static List<(TextSegment Segment, Color Color)> Highlight(string text, CodeLanguage language)
        {
            var result = new List<(TextSegment, Color)>();
            if (string.IsNullOrEmpty(text)) return result;
            var tokenizer = SyntaxTokenizer.Create(language);
            var tokens = tokenizer.Tokenize(text);
            var lineStarts = ComputeLineStarts(text);

            foreach (var t in tokens)
            {
                int startOff = OffsetFromLineCol(lineStarts, t.Line, t.Column);
                string segText = (startOff >= 0 && startOff + t.Length <= text.Length) ? text.Substring(startOff, t.Length) : t.Text;
                var seg = new TextSegment(segText, t.Line, t.Column);
                result.Add((seg, TokenColors.GetValueOrDefault(t.Type, Color.FromArgb(212, 212, 212))));
            }
            return result;
        }

        public static List<int> ComputeLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        public static int OffsetFromLineCol(List<int> lineStarts, int line, int column)
        {
            if (line < 0) line = 0;
            if (line >= lineStarts.Count) line = lineStarts.Count - 1;
            return lineStarts[line] + Math.Max(0, column);
        }

        public static Color GetColor(TokenType type) => TokenColors.GetValueOrDefault(type, Color.FromArgb(212, 212, 212));
    }
}
