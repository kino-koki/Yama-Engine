// ==================================================================================================
//  CodeFormatter.cs — 山雨引擎代码格式化与折叠
//
//  命名空间: ShanyuEngine.CodeEditing
//  依赖:     System, System.Collections.Generic, System.Linq, System.Text
//
//  功能:
//    1. CSharpFormatter   — C# 代码格式化 (4空格/Allman/空格规范化/using排序)
//    2. CodeFolding       — 折叠区域检测 (#region/方法体/类体)
//
//  实现说明:
//    接口设计参考 Roslyn SyntaxRewriter 模式 (VisitXXX 方法), 但实际实现使用字符串处理,
//    不依赖完整编译上下文。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShanyuEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  折叠
    // ════════════════════════════════════════════════════════════════════════════

    public enum FoldKind { Region, Method, Type, Block, Comment }

    public sealed class FoldRange
    {
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public FoldKind Kind { get; init; }
        public string DisplayText { get; init; } = string.Empty;
        public override string ToString() => $"[{Kind}] {StartLine}-{EndLine} {DisplayText}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Roslyn 风格 SyntaxRewriter 接口骨架 (保留集成点)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>模拟 Roslyn SyntaxRewriter 的接口骨架。实际格式化器继承并重写 Visit 方法。</summary>
    public abstract class CodeSyntaxRewriter
    {
        public virtual string VisitDocument(string source) => source;
        public virtual string VisitUsingDirective(string line) => line;
        public virtual string VisitBlock(string block, int depth) => block;
        public virtual string VisitExpression(string expression) => expression;
        public virtual string VisitStatement(string statement, int depth) => statement;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CSharpFormatter — C# 代码格式化器
    // ════════════════════════════════════════════════════════════════════════════

    public static class CSharpFormatter
    {
        /// <summary>格式化 C# 源代码。</summary>
        public static string Format(string source)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;
            var rewriter = new StringSyntaxRewriter();
            source = rewriter.VisitDocument(source);
            source = SortUsings(source);
            source = CollapseBlankLines(source);
            source = TrimTrailingWhitespace(source);
            if (!source.EndsWith('\n')) source += '\n';
            return source;
        }

        /// <summary>仅排序 using 指令。</summary>
        public static string SortUsings(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var usings = new List<(int index, string text, bool isSystem)>();
            var others = new List<(int index, string text)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("using ", StringComparison.Ordinal) && !trimmed.Contains('='))
                {
                    var ns = trimmed.Substring(6).TrimEnd(';').Trim();
                    usings.Add((i, lines[i], ns.StartsWith("System", StringComparison.Ordinal)));
                }
                else others.Add((i, lines[i]));
            }
            if (usings.Count == 0) return source;
            usings.Sort((a, b) =>
            {
                if (a.isSystem != b.isSystem) return a.isSystem ? -1 : 1;
                return string.CompareOrdinal(a.text.Trim(), b.text.Trim());
            });
            var result = new List<string>();
            int u = 0, o = 0;
            int lastUsingIndex = usings[^1].index;
            for (int i = 0; i < lines.Length; i++)
            {
                if (u < usings.Count && usings[u].index == i)
                {
                    result.Add(usings[u].text);
                    u++;
                }
                else if (o < others.Count && others[o].index == i)
                {
                    result.Add(others[o].text);
                    o++;
                }
            }
            return string.Join("\n", result);
        }

        /// <summary>获取折叠范围列表。</summary>
        public static IReadOnlyList<FoldRange> GetFoldRanges(string source)
        {
            var result = new List<FoldRange>();
            if (string.IsNullOrEmpty(source)) return result;
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var stack = new Stack<(int line, string name, FoldKind kind)>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // region
                if (trimmed.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                {
                    var name = trimmed.Length > "#region".Length ? trimmed.Substring("#region".Length).Trim() : "region";
                    stack.Push((i + 1, name, FoldKind.Region));
                }
                else if (trimmed.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase) && stack.Count > 0 && stack.Peek().kind == FoldKind.Region)
                {
                    var (s, n, k) = stack.Pop();
                    result.Add(new FoldRange { StartLine = s, EndLine = i + 1, Kind = k, DisplayText = n });
                }
                // 类型/方法/块: 通过大括号匹配
                else if (trimmed.EndsWith("{") && !trimmed.StartsWith("//") && !trimmed.StartsWith("/*"))
                {
                    string header = trimmed.TrimEnd('{', ' ', '\t');
                    FoldKind kind = FoldKind.Block;
                    string name = header;
                    if (header.StartsWith("class ") || header.StartsWith("struct ") || header.StartsWith("interface ") || header.StartsWith("enum ") || header.StartsWith("record "))
                        kind = FoldKind.Type;
                    else if (header.Contains('(') && (header.Contains("public ") || header.Contains("private ") || header.Contains("protected ") || header.Contains("internal ") || header.Contains("void ") || header.Contains("int ") || header.Contains("float ") || header.Contains("bool ") || header.Contains("string ")))
                        kind = FoldKind.Method;
                    stack.Push((i + 1, name, kind));
                }
                else if (trimmed == "}" && stack.Count > 0)
                {
                    // 弹出最近的非 region 块
                    var temp = new Stack<(int, string, FoldKind)>();
                    while (stack.Count > 0 && stack.Peek().kind == FoldKind.Region) temp.Push(stack.Pop());
                    if (stack.Count > 0)
                    {
                        var (s, n, k) = stack.Pop();
                        if (k != FoldKind.Block || s < i + 1)
                            result.Add(new FoldRange { StartLine = s, EndLine = i + 1, Kind = k, DisplayText = n });
                    }
                    while (temp.Count > 0) stack.Push(temp.Pop());
                }
            }
            return result.OrderBy(f => f.StartLine).ToList();
        }

        // ── 文本后处理 ───────────────────────────────────────────────────────────

        private static string CollapseBlankLines(string text)
        {
            var lines = text.Split('\n');
            var result = new List<string>();
            int blankCount = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) { blankCount++; if (blankCount <= 1) result.Add(string.Empty); }
                else { blankCount = 0; result.Add(line); }
            }
            return string.Join("\n", result);
        }

        private static string TrimTrailingWhitespace(string text)
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd(' ', '\t');
            return string.Join("\n", lines);
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  StringSyntaxRewriter — 基于字符串的模拟 Rewriter
        // ════════════════════════════════════════════════════════════════════════════

        private sealed class StringSyntaxRewriter : CodeSyntaxRewriter
        {
            public override string VisitDocument(string source)
            {
                var lines = source.Replace("\r\n", "\n").Split('\n');
                var result = new List<string>();
                int depth = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) { result.Add(string.Empty); continue; }

                    // 预处理/region 保持原深度
                    if (trimmed.StartsWith("#"))
                    {
                        result.Add(trimmed);
                        continue;
                    }

                    // 注释保持当前深度
                    bool isComment = trimmed.StartsWith("//") || trimmed.StartsWith("/*");

                    // 闭括号降深度
                    int closeBraces = trimmed.TakeWhile(c => c == '}').Count();
                    int openBraces = trimmed.Reverse().TakeWhile(c => c == '{').Count();
                    // 处理行首 } 的情况
                    int lineDepth = depth;
                    if (trimmed.StartsWith("}"))
                    {
                        lineDepth = Math.Max(0, depth - 1);
                        // 统计本行开头的 } 数量来调整后续深度
                        int j = 0;
                        while (j < trimmed.Length && trimmed[j] == '}') { j++; lineDepth = Math.Max(0, lineDepth - (j > 1 ? 1 : 0)); }
                    }

                    // Allman: 如果上一行以 ) 或标识符结尾且本行是 {, 将 { 独立成行
                    if (trimmed == "{" && i > 0 && result.Count > 0)
                    {
                        var prev = result[^1].Trim();
                        if (!string.IsNullOrEmpty(prev) && !prev.EndsWith("{") && !prev.StartsWith("#"))
                        {
                            // 上一行末尾不应该有 {, 而是把 { 单独作为新行
                            result.Add(new string(' ', 4 * depth) + "{");
                            depth++;
                            continue;
                        }
                    }

                    // 缩进
                    string indented = isComment ? new string(' ', 4 * depth) + trimmed : new string(' ', 4 * lineDepth) + ProcessLine(trimmed);
                    result.Add(indented);

                    // 深度更新 (基于行尾)
                    int openCount = trimmed.Count(c => c == '{');
                    int closeCount = trimmed.Count(c => c == '}');
                    // 若行首有 }, 已在 lineDepth 中减过一次
                    if (trimmed.StartsWith("}")) closeCount = Math.Max(0, closeCount - 1);
                    depth += openCount - closeCount;
                    depth = Math.Max(0, depth);
                }
                return string.Join("\n", result);
            }

            private static string ProcessLine(string line)
            {
                // 保留字符串和注释不被破坏 (简化: 不在字符串内替换)
                var sb = new StringBuilder();
                bool inString = false;
                bool inChar = false;
                bool inLineComment = false;
                bool inBlockComment = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    char prev = i > 0 ? line[i - 1] : '\0';
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';

                    if (inLineComment) { sb.Append(c); continue; }
                    if (inBlockComment)
                    {
                        sb.Append(c);
                        if (c == '*' && next == '/') { inBlockComment = false; if (i + 1 < line.Length) sb.Append('/'); i++; }
                        continue;
                    }
                    if (inString)
                    {
                        sb.Append(c);
                        if (c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); }
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (inChar)
                    {
                        sb.Append(c);
                        if (c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); }
                        else if (c == '\'') inChar = false;
                        continue;
                    }
                    if (c == '/' && next == '/') { sb.Append(c); inLineComment = true; continue; }
                    if (c == '/' && next == '*') { sb.Append(c); inBlockComment = true; continue; }
                    if (c == '"') { sb.Append(c); inString = true; continue; }
                    if (c == '\'') { sb.Append(c); inChar = true; continue; }

                    // 空格规范化
                    if (c == ' ' || c == '\t')
                    {
                        // 避免多个连续空格 (保留已有一个空格时跳过额外空格)
                        if (sb.Length > 0 && sb[sb.Length - 1] == ' ') continue;
                        sb.Append(' ');
                        continue;
                    }

                    // if/for/while/switch/foreach/catch 后 ( 前加空格
                    if (c == '(')
                    {
                        string prevWord = GetPreviousWord(sb);
                        if (prevWord is "if" or "for" or "while" or "switch" or "foreach" or "catch" or "using" or "lock" or "fixed")
                        {
                            if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                        }
                        sb.Append(c);
                        continue;
                    }

                    // 运算符前后空格: + - * / % = == != < > <= >= && || ?? ?. += -= *= /= ++ -- -> << >> & | ^ ~
                    if (IsOperatorChar(c))
                    {
                        string op = ReadOperator(line, i);
                        if (op is "+" or "-" or "*" or "/" or "%" or "=" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "??" or "?." or "+=" or "-=" or "*=" or "/=" or "<<" or ">>" or "&" or "|" or "^" or "~")
                        {
                            // 一元 +/- 前面不加空格 (如 -1, ++i, i++)
                            bool isUnary = (op == "+" || op == "-" || op == "++" || op == "--") && IsUnaryContext(sb);
                            if (!isUnary && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                            sb.Append(op);
                            i += op.Length - 1;
                            if (!isUnary && i + 1 < line.Length && line[i + 1] != ' ' && line[i + 1] != ';' && line[i + 1] != ',' && line[i + 1] != ')')
                                sb.Append(' ');
                            continue;
                        }
                        // -> 箭头前后不加空格
                        if (op == "->")
                        {
                            sb.Append(op);
                            i += 1;
                            continue;
                        }
                        sb.Append(op);
                        i += op.Length - 1;
                        continue;
                    }

                    // ; 和 , 后加空格 (但不在行尾)
                    if (c == ';' || c == ',')
                    {
                        sb.Append(c);
                        if (i + 1 < line.Length && line[i + 1] != ' ' && line[i + 1] != ')')
                            sb.Append(' ');
                        continue;
                    }

                    sb.Append(c);
                }
                return sb.ToString().Trim();
            }

            private static string GetPreviousWord(StringBuilder sb)
            {
                int i = sb.Length - 1;
                while (i >= 0 && sb[i] == ' ') i--;
                int end = i;
                while (i >= 0 && (char.IsLetterOrDigit(sb[i]) || sb[i] == '_')) i--;
                return sb.ToString(i + 1, end - i);
            }

            private static bool IsOperatorChar(char c) => "+-*/%=<>!&|^~?".Contains(c);

            private static string ReadOperator(string line, int start)
            {
                int len = 1;
                if (start + 1 < line.Length)
                {
                    string two = line.Substring(start, 2);
                    if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "?." or "+=" or "-=" or "*=" or "/=" or "->" or "++" or "--" or "<<" or ">>")
                        len = 2;
                }
                return line.Substring(start, len);
            }

            private static bool IsUnaryContext(StringBuilder sb)
            {
                if (sb.Length == 0) return true;
                char last = sb[sb.Length - 1];
                if (last == '(' || last == '[' || last == '{' || last == '=' || last == ',' || last == ';') return true;
                if (char.IsWhiteSpace(last)) return true; // 简化
                return false;
            }
        }
    }
}
