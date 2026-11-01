// ==================================================================================================
//  IntelliSenseEngine.cs — yama引擎代码智能感知
//
//  命名空间: YamaEngine.CodeEditing
//  依赖:     Microsoft.CodeAnalysis (Roslyn 接口), System
//
//  功能:
//    1. IntelliSenseProvider — 基于字符串分析与字典匹配的 C# 智能补全
//         - GetCompletionItems : 成员补全 / 变量名补全 / 命名空间补全 / 触发过滤
//         - GetSignatureHelp   : 方法签名提示 (输入 '(' 触发)
//         - GetQuickInfo       : 悬停提示 + 错误波浪线支持
//         - GoToDefinition     : F12 跳转定义 (接口保留 Roslyn SymbolFinder 集成点)
//    2. CompletionItem / SignatureHelp / QuickInfo / DefinitionLocation 数据模型
//
//  说明:
//    因运行时难以维护完整编译上下文, 默认实现使用字符串分析与字典匹配模拟。
//    接口层面保留 Microsoft.CodeAnalysis.Compilation 集成点, 支持未来接入真实 Roslyn。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YamaEngine.CodeEditing
{
    // ════════════════════════════════════════════════════════════════════════════
    //  数据模型
    // ════════════════════════════════════════════════════════════════════════════

    public enum CompletionIconType
    {
        Class, Method, Property, Field, Event, Namespace, Keyword,
        Enum, Struct, Interface, Snippet, Variable, Text
    }

    public sealed class CompletionItem
    {
        public string DisplayText { get; init; } = string.Empty;
        public string InsertText { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public CompletionIconType IconType { get; init; } = CompletionIconType.Text;
        public string SortText { get; init; } = string.Empty;
        public string FilterText { get; init; } = string.Empty;
        public override string ToString() => $"{IconType}: {DisplayText}";
    }

    public sealed class SignatureParameter
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Documentation { get; init; } = string.Empty;
    }

    public sealed class SignatureInfo
    {
        public string DisplayString { get; init; } = string.Empty;
        public string ReturnType { get; init; } = string.Empty;
        public string Documentation { get; init; } = string.Empty;
        public IReadOnlyList<SignatureParameter> Parameters { get; init; } = Array.Empty<SignatureParameter>();
    }

    public sealed class SignatureHelp
    {
        public IReadOnlyList<SignatureInfo> Signatures { get; init; } = Array.Empty<SignatureInfo>();
        public int ActiveSignature { get; init; }
        public int ActiveParameter { get; init; }
        public bool IsActive => Signatures.Count > 0;
    }

    public sealed class QuickInfo
    {
        public string Content { get; init; } = string.Empty;
        public CompletionIconType Kind { get; init; } = CompletionIconType.Text;
        public TextRange Span { get; init; }
        public bool HasError { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public bool IsValid => !string.IsNullOrEmpty(Content) || HasError;
    }

    public sealed class DefinitionLocation
    {
        public string FilePath { get; init; } = string.Empty;
        public int Line { get; init; }
        public int Column { get; init; }
        public string ContainingSymbol { get; init; } = string.Empty;
        public bool IsExternal { get; init; }
        public bool IsValid => !string.IsNullOrEmpty(FilePath);
    }

    public enum CompletionTriggerKind
    {
        Invoked, MemberAccess, Explicit
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  IntelliSenseProvider — 智能感知提供器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 智能感知提供器。默认实现基于字符串分析与字典匹配;
    /// 可通过 <see cref="AttachCompilation"/> 接入 Roslyn Compilation 进行增强。
    /// </summary>
    public sealed class IntelliSenseProvider
    {
        // ── Roslyn 集成点 ────────────────────────────────────────────────────────
        private Compilation? _compilation;
        private SyntaxTree? _tree;

        /// <summary>接入真实 Roslyn Compilation (可选)。</summary>
        public void AttachCompilation(Compilation? compilation, SyntaxTree? tree)
        {
            _compilation = compilation;
            _tree = tree;
        }

        public void DetachCompilation() { _compilation = null; _tree = null; }

        public bool HasRoslynBackend => _compilation != null && _tree != null;

        /// <summary>释放资源（当前无原生资源，保留接口以支持 IDisposable 风格调用）。</summary>
        public void Dispose() { }

        // ── 内置字典 ─────────────────────────────────────────────────────────────
        private static readonly HashSet<string> KeywordList = new()
        {
            "abstract","as","async","await","base","bool","break","byte","case","catch","char",
            "checked","class","const","continue","decimal","default","delegate","do","double",
            "else","enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","get","goto","if","implicit","in","int","interface","internal","is","lock",
            "long","namespace","new","null","object","operator","out","override","params","partial",
            "private","protected","public","readonly","ref","return","sbyte","sealed","set","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try",
            "typeof","uint","ulong","unchecked","unsafe","ushort","using","var","virtual","void",
            "volatile","while","yield","nameof","when","where","record","init","required","file"
        };

        private static readonly HashSet<string> CommonTypes = new()
        {
            "object","string","int","float","double","bool","char","decimal","byte","sbyte",
            "short","ushort","long","ulong","uint","nint","nuint","dynamic","var",
            "Vector2","Vector3","Vector4","Quaternion","Matrix4x4","Transform","Entity",
            "List","Dictionary","HashSet","Array","Span","ReadOnlySpan","Task","Action","Func",
            "Color","StringBuilder","IEnumerable","IDisposable","Exception","DateTime","TimeSpan"
        };

        private static readonly Dictionary<string, List<SignatureInfo>> MethodSignatures = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Console.WriteLine"] = new()
            {
                new SignatureInfo { DisplayString = "void Console.WriteLine(string? value)", ReturnType = "void", Parameters = new[] { new SignatureParameter { Name = "value", Type = "string?" } } },
                new SignatureInfo { DisplayString = "void Console.WriteLine(object? value)", ReturnType = "void", Parameters = new[] { new SignatureParameter { Name = "value", Type = "object?" } } },
                new SignatureInfo { DisplayString = "void Console.WriteLine(string format, params object?[] args)", ReturnType = "void", Parameters = new[] { new SignatureParameter { Name = "format", Type = "string" }, new SignatureParameter { Name = "args", Type = "object?[]" } } }
            },
            ["Log"] = new()
            {
                new SignatureInfo { DisplayString = "void Log(string message)", ReturnType = "void", Parameters = new[] { new SignatureParameter { Name = "message", Type = "string" } } }
            },
            ["Vector3.Distance"] = new()
            {
                new SignatureInfo { DisplayString = "float Vector3.Distance(Vector3 a, Vector3 b)", ReturnType = "float", Parameters = new[] { new SignatureParameter { Name = "a", Type = "Vector3" }, new SignatureParameter { Name = "b", Type = "Vector3" } } }
            },
            ["Vector3.Lerp"] = new()
            {
                new SignatureInfo { DisplayString = "Vector3 Vector3.Lerp(Vector3 a, Vector3 b, float t)", ReturnType = "Vector3", Parameters = new[] { new SignatureParameter { Name = "a", Type = "Vector3" }, new SignatureParameter { Name = "b", Type = "Vector3" }, new SignatureParameter { Name = "t", Type = "float" } } }
            },
            ["List.Add"] = new()
            {
                new SignatureInfo { DisplayString = "void List<T>.Add(T item)", ReturnType = "void", Parameters = new[] { new SignatureParameter { Name = "item", Type = "T" } } }
            },
            ["List.Remove"] = new()
            {
                new SignatureInfo { DisplayString = "bool List<T>.Remove(T item)", ReturnType = "bool", Parameters = new[] { new SignatureParameter { Name = "item", Type = "T" } } }
            },
            ["string.IsNullOrEmpty"] = new()
            {
                new SignatureInfo { DisplayString = "bool string.IsNullOrEmpty(string? value)", ReturnType = "bool", Parameters = new[] { new SignatureParameter { Name = "value", Type = "string?" } } }
            },
            ["string.Format"] = new()
            {
                new SignatureInfo { DisplayString = "string string.Format(string format, params object?[] args)", ReturnType = "string", Parameters = new[] { new SignatureParameter { Name = "format", Type = "string" }, new SignatureParameter { Name = "args", Type = "object?[]" } } }
            },
            ["Math.Clamp"] = new()
            {
                new SignatureInfo { DisplayString = "T Math.Clamp(T value, T min, T max)", ReturnType = "T", Parameters = new[] { new SignatureParameter { Name = "value", Type = "T" }, new SignatureParameter { Name = "min", Type = "T" }, new SignatureParameter { Name = "max", Type = "T" } } }
            },
            ["Math.Max"] = new()
            {
                new SignatureInfo { DisplayString = "T Math.Max(T a, T b)", ReturnType = "T", Parameters = new[] { new SignatureParameter { Name = "a", Type = "T" }, new SignatureParameter { Name = "b", Type = "T" } } }
            },
            ["Math.Min"] = new()
            {
                new SignatureInfo { DisplayString = "T Math.Min(T a, T b)", ReturnType = "T", Parameters = new[] { new SignatureParameter { Name = "a", Type = "T" }, new SignatureParameter { Name = "b", Type = "T" } } }
            },
        };

        private static readonly Dictionary<string, string> QuickInfoDocs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["int"] = "struct System.Int32: 32 位有符号整数类型。",
            ["float"] = "struct System.Single: 32 位 IEEE 浮点数。",
            ["double"] = "struct System.Double: 64 位 IEEE 浮点数。",
            ["bool"] = "struct System.Boolean: 布尔类型 (true / false)。",
            ["string"] = "class System.String: 不可变 Unicode 字符序列。",
            ["var"] = "隐式类型局部变量。编译器根据右侧表达式推断类型。",
            ["Vector3"] = "struct Vector3: 三维向量 (X, Y, Z)。",
            ["Quaternion"] = "struct Quaternion: 表示三维旋转。",
            ["List"] = "class List<T>: 动态数组, 可变大小。",
            ["Dictionary"] = "class Dictionary<TKey, TValue>: 键值对集合。",
            ["Console.WriteLine"] = "将指定数据后跟当前行终止符写入标准输出流。",
            ["Log"] = "输出日志消息到引擎控制台。",
            ["using"] = "导入命名空间或确保 IDisposable 对象被释放。",
            ["namespace"] = "声明一个范围, 用于组织相关对象。",
            ["class"] = "声明引用类型。",
            ["struct"] = "声明值类型。",
            ["interface"] = "定义一个协定, 实现类必须提供协定成员。",
            ["enum"] = "声明一组命名的常数。",
            ["return"] = "终止当前方法的执行并返回结果。",
            ["if"] = "条件语句: 根据布尔表达式选择执行路径。",
            ["else"] = "与 if 配对, 条件为假时执行。",
            ["for"] = "循环语句: 初始化、条件、迭代器。",
            ["while"] = "循环语句: 条件为真时重复执行。",
            ["foreach"] = "循环语句: 遍历集合的每个元素。",
            ["try"] = "定义一个代码块, 监视运行时错误。",
            ["catch"] = "处理 try 块中引发的异常。",
            ["finally"] = "定义一个代码块, 无论异常是否发生都会执行。",
            ["break"] = "终止最近的封闭循环或 switch 语句。",
            ["continue"] = "将控制传递给最近的封闭循环的下一次迭代。",
            ["new"] = "创建类型的新实例。",
            ["null"] = "表示不引用任何对象的空引用。",
            ["void"] = "指定方法不返回值。",
            ["static"] = "声明属于类型本身而不是特定对象的成员。",
            ["public"] = "访问修饰符: 不受限制地访问。",
            ["private"] = "访问修饰符: 仅在同一体内访问。",
            ["protected"] = "访问修饰符: 仅在同类或派生类中访问。",
            ["internal"] = "访问修饰符: 仅在同一程序集中访问。",
            ["readonly"] = "声明字段只能在声明时或构造函数中赋值。",
            ["const"] = "声明编译时常量。",
            ["async"] = "声明方法为异步方法。",
            ["await"] = "挂起异步方法的执行直到等待的任务完成。",
            ["override"] = "扩展或修改继承的虚/抽象/重写成员的实现。",
            ["virtual"] = "声明可在派生类中重写的方法。",
            ["abstract"] = "声明必须在派生类中实现的类或成员。",
            ["sealed"] = "阻止类被继承或成员被重写。",
            ["delegate"] = "声明引用类型, 可用于封装命名方法或匿名方法。",
            ["event"] = "声明类或结构在发生某些事情时通知其他对象的方式。",
            ["get"] = "定义属性访问器以检索属性值。",
            ["set"] = "定义属性访问器以分配属性值。",
        };

        // ── 补全 ─────────────────────────────────────────────────────────────────

        public IReadOnlyList<CompletionItem> GetCompletionItems(CodeDocument document, int position)
        {
            if (document == null || position < 0 || position > document.Text.Length)
                return Array.Empty<CompletionItem>();

            var text = document.Text;
            var trigger = DetectTrigger(text, position, out string prefix, out string target);
            var items = new List<CompletionItem>();

            if (trigger == CompletionTriggerKind.MemberAccess && !string.IsNullOrEmpty(target))
            {
                items.AddRange(GetMemberCompletions(target));
            }
            else
            {
                items.AddRange(GetKeywordCompletions(prefix));
                items.AddRange(GetTypeCompletions(prefix));
                items.AddRange(GetSnippetCompletions(prefix));
                items.AddRange(GetLocalVariableCompletions(document, prefix));
            }

            return FilterAndSort(items, prefix);
        }

        private CompletionTriggerKind DetectTrigger(string text, int position, out string prefix, out string target)
        {
            prefix = string.Empty;
            target = string.Empty;
            if (position <= 0) return CompletionTriggerKind.Invoked;

            int i = position - 1;
            // 跳过紧挨光标的空白
            while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
            if (i < 0) return CompletionTriggerKind.Invoked;

            // 成员访问: 目标.
            if (text[i] == '.')
            {
                int end = i;
                i--;
                while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == ')')) i--;
                target = text.Substring(i + 1, end - (i + 1)).Trim();
                if (target.EndsWith(")"))
                {
                    // 简化: 从倒数第一个 ( 提取方法调用链前面的标识符
                    int paren = target.LastIndexOf('(');
                    if (paren > 0) target = target.Substring(0, paren).Trim();
                }
                return CompletionTriggerKind.MemberAccess;
            }

            // 提取前缀
            int start = position - 1;
            while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_')) start--;
            prefix = text.Substring(start + 1, position - start - 1);
            return CompletionTriggerKind.Invoked;
        }

        private IEnumerable<CompletionItem> GetMemberCompletions(string target)
        {
            var dict = new Dictionary<string, CompletionItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in MethodSignatures)
            {
                if (kv.Key.StartsWith(target + ".", StringComparison.OrdinalIgnoreCase))
                {
                    string name = kv.Key.Substring(target.Length + 1);
                    if (!dict.ContainsKey(name))
                        dict[name] = new CompletionItem { DisplayText = name, InsertText = name, IconType = CompletionIconType.Method, FilterText = name, SortText = name };
                }
            }

            // 通用成员启发
            if (target.Equals("List", StringComparison.OrdinalIgnoreCase) || target.StartsWith("List<", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(dict, "Add", CompletionIconType.Method);
                AddIfMissing(dict, "Remove", CompletionIconType.Method);
                AddIfMissing(dict, "Clear", CompletionIconType.Method);
                AddIfMissing(dict, "Count", CompletionIconType.Property);
                AddIfMissing(dict, "Find", CompletionIconType.Method);
                AddIfMissing(dict, "Sort", CompletionIconType.Method);
            }
            if (target.Equals("Dictionary", StringComparison.OrdinalIgnoreCase) || target.StartsWith("Dictionary<", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(dict, "Add", CompletionIconType.Method);
                AddIfMissing(dict, "Remove", CompletionIconType.Method);
                AddIfMissing(dict, "Clear", CompletionIconType.Method);
                AddIfMissing(dict, "ContainsKey", CompletionIconType.Method);
                AddIfMissing(dict, "TryGetValue", CompletionIconType.Method);
                AddIfMissing(dict, "Count", CompletionIconType.Property);
                AddIfMissing(dict, "Keys", CompletionIconType.Property);
                AddIfMissing(dict, "Values", CompletionIconType.Property);
            }
            if (target.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(dict, "Length", CompletionIconType.Property);
                AddIfMissing(dict, "Substring", CompletionIconType.Method);
                AddIfMissing(dict, "Split", CompletionIconType.Method);
                AddIfMissing(dict, "Trim", CompletionIconType.Method);
                AddIfMissing(dict, "Contains", CompletionIconType.Method);
                AddIfMissing(dict, "Replace", CompletionIconType.Method);
                AddIfMissing(dict, "ToLower", CompletionIconType.Method);
                AddIfMissing(dict, "ToUpper", CompletionIconType.Method);
                AddIfMissing(dict, "IsNullOrEmpty", CompletionIconType.Method);
                AddIfMissing(dict, "Format", CompletionIconType.Method);
            }
            if (target.Equals("Vector3", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(dict, "x", CompletionIconType.Field);
                AddIfMissing(dict, "y", CompletionIconType.Field);
                AddIfMissing(dict, "z", CompletionIconType.Field);
                AddIfMissing(dict, "Distance", CompletionIconType.Method);
                AddIfMissing(dict, "Lerp", CompletionIconType.Method);
                AddIfMissing(dict, "Normalize", CompletionIconType.Method);
                AddIfMissing(dict, "Dot", CompletionIconType.Method);
                AddIfMissing(dict, "Cross", CompletionIconType.Method);
                AddIfMissing(dict, "Magnitude", CompletionIconType.Property);
                AddIfMissing(dict, "Zero", CompletionIconType.Field);
                AddIfMissing(dict, "One", CompletionIconType.Field);
                AddIfMissing(dict, "Up", CompletionIconType.Field);
                AddIfMissing(dict, "Forward", CompletionIconType.Field);
            }
            if (target.Equals("Console", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(dict, "WriteLine", CompletionIconType.Method);
                AddIfMissing(dict, "Write", CompletionIconType.Method);
                AddIfMissing(dict, "ReadLine", CompletionIconType.Method);
            }

            return dict.Values;
        }

        private static void AddIfMissing(Dictionary<string, CompletionItem> dict, string name, CompletionIconType icon)
        {
            if (!dict.ContainsKey(name))
                dict[name] = new CompletionItem { DisplayText = name, InsertText = name, IconType = icon, FilterText = name, SortText = name };
        }

        private IEnumerable<CompletionItem> GetKeywordCompletions(string prefix)
        {
            foreach (var kw in KeywordList)
            {
                if (string.IsNullOrEmpty(prefix) || kw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return new CompletionItem { DisplayText = kw, InsertText = kw, IconType = CompletionIconType.Keyword, FilterText = kw, SortText = "0_" + kw };
            }
        }

        private IEnumerable<CompletionItem> GetTypeCompletions(string prefix)
        {
            foreach (var t in CommonTypes)
            {
                if (string.IsNullOrEmpty(prefix) || t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return new CompletionItem { DisplayText = t, InsertText = t, IconType = CompletionIconType.Class, FilterText = t, SortText = "1_" + t };
            }
        }

        private IEnumerable<CompletionItem> GetSnippetCompletions(string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || "cw".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionItem { DisplayText = "cw", InsertText = "Console.WriteLine($\"\");", IconType = CompletionIconType.Snippet, FilterText = "cw", SortText = "9_cw" };
            if (string.IsNullOrEmpty(prefix) || "for".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionItem { DisplayText = "for", InsertText = "for (int i = 0; i < length; i++)\n{\n}", IconType = CompletionIconType.Snippet, FilterText = "for", SortText = "9_for" };
            if (string.IsNullOrEmpty(prefix) || "foreach".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionItem { DisplayText = "foreach", InsertText = "foreach (var item in collection)\n{\n}", IconType = CompletionIconType.Snippet, FilterText = "foreach", SortText = "9_foreach" };
            if (string.IsNullOrEmpty(prefix) || "if".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionItem { DisplayText = "if", InsertText = "if (condition)\n{\n}", IconType = CompletionIconType.Snippet, FilterText = "if", SortText = "9_if" };
        }

        private IEnumerable<CompletionItem> GetLocalVariableCompletions(CodeDocument document, string prefix)
        {
            // 简单启发: 扫描文档中已声明的局部变量/字段/参数名
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var text = document.Text;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // 非常简化的模式匹配: 类型 名称 [= ...];
                var parts = trimmed.Split(new[] { ' ', '\t', '(', ')', '{', '}', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string type = parts[i];
                    string name = parts[i + 1];
                    if (CommonTypes.Contains(type) || char.IsUpper(type[0]))
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !KeywordList.Contains(name) && found.Add(name))
                            yield return new CompletionItem { DisplayText = name, InsertText = name, IconType = CompletionIconType.Variable, FilterText = name, SortText = "2_" + name };
                    }
                }
            }
        }

        private static IReadOnlyList<CompletionItem> FilterAndSort(List<CompletionItem> items, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return items.OrderBy(i => i.SortText, StringComparer.OrdinalIgnoreCase).ToList();

            var starts = new List<CompletionItem>();
            var contains = new List<CompletionItem>();
            foreach (var i in items)
            {
                if (i.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    starts.Add(i);
                else if (i.FilterText.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    contains.Add(i);
            }
            starts.Sort((a, b) => string.CompareOrdinal(a.SortText, b.SortText));
            contains.Sort((a, b) => string.CompareOrdinal(a.SortText, b.SortText));
            starts.AddRange(contains);
            return starts;
        }

        // ── 签名提示 ─────────────────────────────────────────────────────────────

        public SignatureHelp? GetSignatureHelp(CodeDocument document, int position)
        {
            if (document == null || position <= 0) return null;
            var text = document.Text;
            // 反向查找方法名和当前参数索引
            int parenDepth = 0;
            int commaCount = 0;
            int i = position - 1;
            // 如果光标在 ) 上, 向前找匹配的 (
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ')')) i++;
            i = position - 1;
            while (i >= 0)
            {
                char c = text[i];
                if (c == ')') parenDepth++;
                else if (c == '(')
                {
                    if (parenDepth == 0) break;
                    parenDepth--;
                }
                else if (c == ',' && parenDepth == 0) commaCount++;
                i--;
            }
            if (i < 0) return null;
            // 提取方法名
            int methodEnd = i;
            i--;
            while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
            int methodStart = i;
            while (methodStart >= 0 && (char.IsLetterOrDigit(text[methodStart]) || text[methodStart] == '_' || text[methodStart] == '.')) methodStart--;
            string methodName = text.Substring(methodStart + 1, methodEnd - (methodStart + 1)).Trim();

            // 尝试查找签名
            if (MethodSignatures.TryGetValue(methodName, out var sigs))
                return new SignatureHelp { Signatures = sigs, ActiveSignature = 0, ActiveParameter = commaCount };

            // 简化: 仅方法名匹配 (不含类前缀)
            string lastPart = methodName.Contains('.') ? methodName.Substring(methodName.LastIndexOf('.') + 1) : methodName;
            foreach (var kv in MethodSignatures)
            {
                if (kv.Key.EndsWith("." + lastPart, StringComparison.OrdinalIgnoreCase))
                    return new SignatureHelp { Signatures = kv.Value, ActiveSignature = 0, ActiveParameter = commaCount };
            }

            // Roslyn 集成点: 若存在后端, 可在此调用真实语义分析
            if (HasRoslynBackend)
            {
                // 占位: 真实实现可通过 _compilation / _tree 获取 IMethodSymbol
                // 当前回退到空结果以维持无编译依赖的默认行为
            }

            return null;
        }

        // ── 悬停提示 ─────────────────────────────────────────────────────────────

        public QuickInfo? GetQuickInfo(CodeDocument document, int position)
        {
            if (document == null || position < 0 || position >= document.Text.Length) return null;
            var text = document.Text;
            int start = position;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
            int end = position;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            string word = text.Substring(start, end - start);
            if (string.IsNullOrEmpty(word)) return null;

            // 尝试精确匹配
            if (QuickInfoDocs.TryGetValue(word, out var doc))
            {
                var icon = KeywordList.Contains(word) ? CompletionIconType.Keyword : CompletionIconType.Class;
                return new QuickInfo { Content = doc, Kind = icon, Span = new TextRange(document.OffsetToPosition(start), document.OffsetToPosition(end)) };
            }

            // 尝试成员匹配
            foreach (var kv in QuickInfoDocs)
            {
                if (kv.Key.EndsWith("." + word, StringComparison.OrdinalIgnoreCase))
                    return new QuickInfo { Content = kv.Value, Kind = CompletionIconType.Method, Span = new TextRange(document.OffsetToPosition(start), document.OffsetToPosition(end)) };
            }

            // Roslyn 集成点
            if (HasRoslynBackend)
            {
                // 占位: 可通过 _model.GetSymbolInfo 获取真实文档
            }

            return null;
        }

        // ── 跳转定义 ─────────────────────────────────────────────────────────────

        public DefinitionLocation? GoToDefinition(CodeDocument document, int position)
        {
            if (document == null || position < 0 || position >= document.Text.Length) return null;
            var text = document.Text;
            int start = position;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
            int end = position;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            string word = text.Substring(start, end - start);
            if (string.IsNullOrEmpty(word)) return null;

            // 启发式: 在当前文档内搜索类/方法/属性定义
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                if (line.Contains("class " + word + " ") || line.Contains("struct " + word + " ")
                    || line.Contains("interface " + word + " ") || line.Contains("enum " + word + " ")
                    || line.Contains(word + "(") || line.Contains(word + " {") || line.Contains(word + " =>"))
                {
                    return new DefinitionLocation
                    {
                        FilePath = document.FilePath,
                        Line = lineIdx + 1,
                        Column = line.IndexOf(word, StringComparison.OrdinalIgnoreCase) + 1,
                        ContainingSymbol = word,
                        IsExternal = false
                    };
                }
            }

            // Roslyn SymbolFinder 集成点
            if (HasRoslynBackend)
            {
                // 占位: 可通过 SymbolFinder.FindSourceDefinitionAsync 获取真实位置
                return new DefinitionLocation
                {
                    FilePath = $"roslyn://symbols/{word}",
                    Line = 0, Column = 0, ContainingSymbol = word, IsExternal = true
                };
            }

            return null;
        }

        // ── 诊断 (错误波浪线) ────────────────────────────────────────────────────

        public IReadOnlyList<(TextRange Span, string Message, CodeDiagnosticSeverity Severity)> GetDiagnostics(CodeDocument document)
        {
            var result = new List<(TextRange, string, CodeDiagnosticSeverity)>();
            if (document == null) return result;
            var text = document.Text;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int braceBalance = 0;
            int parenBalance = 0;
            bool inString = false;
            bool inBlockComment = false;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                for (int col = 0; col < line.Length; col++)
                {
                    char c = line[col];
                    char next = col + 1 < line.Length ? line[col + 1] : '\0';
                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/') { inBlockComment = false; col++; }
                        continue;
                    }
                    if (inString)
                    {
                        if (c == '\\') { col++; continue; }
                        if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '/' && next == '/') break;
                    if (c == '/' && next == '*') { inBlockComment = true; col++; continue; }
                    if (c == '"') { inString = true; continue; }
                    if (c == '{') braceBalance++;
                    if (c == '}') { braceBalance--; if (braceBalance < 0) { result.Add((new TextRange(new TextPosition(lineIdx, col), new TextPosition(lineIdx, col + 1)), "Unexpected '}'", CodeDiagnosticSeverity.Error)); braceBalance = 0; } }
                    if (c == '(') parenBalance++;
                    if (c == ')') { parenBalance--; if (parenBalance < 0) { result.Add((new TextRange(new TextPosition(lineIdx, col), new TextPosition(lineIdx, col + 1)), "Unexpected ')'", CodeDiagnosticSeverity.Error)); parenBalance = 0; } }
                }
            }
            if (braceBalance > 0) result.Add((new TextRange(new TextPosition(lines.Length - 1, 0), new TextPosition(lines.Length - 1, 0)), "Missing closing brace", CodeDiagnosticSeverity.Error));
            if (parenBalance > 0) result.Add((new TextRange(new TextPosition(lines.Length - 1, 0), new TextPosition(lines.Length - 1, 0)), "Missing closing parenthesis", CodeDiagnosticSeverity.Error));
            if (inString) result.Add((new TextRange(new TextPosition(lines.Length - 1, 0), new TextPosition(lines.Length - 1, 0)), "Unterminated string literal", CodeDiagnosticSeverity.Error));
            if (inBlockComment) result.Add((new TextRange(new TextPosition(lines.Length - 1, 0), new TextPosition(lines.Length - 1, 0)), "Unterminated comment", CodeDiagnosticSeverity.Error));
            return result;
        }
    }
}
