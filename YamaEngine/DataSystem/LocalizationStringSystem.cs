using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YamaEngine.DataSystem
{
    // ====================================================================
    // 本地化字符串系统（LocalizationStringSystem）
    // ====================================================================
    // 类似 UE FText / Unity Localization。
    // 提供运行时本地化文本、格式化参数、复数、性别变体。
    // 与 Core.LocalizationSystem 互补，提供轻量级的 FText 级别抽象。
    // ====================================================================

    /// <summary>
    /// 字符串标记类型。标识文本的本地化语义。
    /// </summary>
    public enum FTextMarkupType
    {
        /// <summary>纯字面量（不翻译）</summary>
        Literal,
        /// <summary>可翻译文本</summary>
        Translatable,
        /// <summary>复数形式</summary>
        Plural,
        /// <summary>性别变体</summary>
        Gender,
        /// <summary>时长格式化</summary>
        Duration
    }

    /// <summary>
    /// FText：本地化文本。yama引擎的核心文本类型。
    /// 携带 Key、Namespace、格式化参数和标记类型。
    /// 类似 UE FText，支持延迟解析和运行时语言切换。
    /// </summary>
    public sealed class FText
    {
        /// <summary>文本标记类型</summary>
        public FTextMarkupType MarkupType { get; }

        /// <summary>本地化键（命名空间.键）</summary>
        public string Key { get; }

        /// <summary>命名空间</summary>
        public string Namespace { get; }

        /// <summary>源字符串（未本地化的原始文本或格式模板）</summary>
        public string SourceString { get; }

        /// <summary>格式化参数列表</summary>
        public List<FTextArgument> Arguments { get; }

        /// <summary>格式化历史记录（用于调试）</summary>
        internal string? _lastFormattedText;

        public FText(FTextMarkupType markupType, string key, string namespaceName, string sourceString)
        {
            MarkupType = markupType;
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Namespace = namespaceName ?? "Default";
            SourceString = sourceString ?? "";
            Arguments = new List<FTextArgument>();
        }

        /// <summary>获取完整键名</summary>
        public string FullKey => string.IsNullOrEmpty(Namespace) ? Key : $"{Namespace}.{Key}";

        /// <summary>是否为空文本</summary>
        public bool IsEmpty => string.IsNullOrEmpty(SourceString) && Arguments.Count == 0;

        /// <summary>是否需要本地化</summary>
        public bool IsTranslatable => MarkupType == FTextMarkupType.Translatable ||
                                       MarkupType == FTextMarkupType.Plural ||
                                       MarkupType == FTextMarkupType.Gender;

        /// <summary>添加格式化参数</summary>
        public FText WithArgument(FTextArgument argument)
        {
            Arguments.Add(argument);
            return this;
        }

        /// <summary>添加命名格式化参数（字符串值）</summary>
        public FText With(string name, string value)
        {
            Arguments.Add(new FTextArgument(name, value));
            return this;
        }

        /// <summary>添加命名格式化参数（整数值）</summary>
        public FText With(string name, int value)
        {
            Arguments.Add(new FTextArgument(name, value));
            return this;
        }

        /// <summary>添加命名格式化参数（浮点值）</summary>
        public FText With(string name, float value)
        {
            Arguments.Add(new FTextArgument(name, value));
            return this;
        }

        /// <summary>添加命名格式化参数（FText 嵌套）</summary>
        public FText With(string name, FText value)
        {
            Arguments.Add(new FTextArgument(name, value));
            return this;
        }

        /// <summary>
        /// 格式化文本。将参数替换到源字符串模板中。
        /// 模板中使用 {name} 占位符。
        /// </summary>
        public string Format()
        {
            if (MarkupType == FTextMarkupType.Literal)
            {
                _lastFormattedText = SourceString;
                return _lastFormattedText;
            }

            string text = SourceString;
            foreach (var arg in Arguments)
            {
                text = ReplaceArgument(text, arg);
            }

            _lastFormattedText = text;
            return text;
        }

        /// <summary>使用指定的格式化器格式化</summary>
        public string Format(FTextFormatter formatter)
        {
            if (formatter == null) return Format();
            return formatter.Format(this);
        }

        private static string ReplaceArgument(string text, FTextArgument arg)
        {
            string placeholder = $"{{{arg.Name}}}";
            string value = arg.GetValueString();

            // 数字格式化
            if (arg.Value is int intVal)
            {
                value = intVal.ToString(CultureInfo.InvariantCulture);
            }
            else if (arg.Value is float floatVal)
            {
                value = floatVal.ToString("F2", CultureInfo.InvariantCulture);
            }
            else if (arg.Value is FText nestedText)
            {
                value = nestedText.Format();
            }

            return text.Replace(placeholder, value);
        }

        /// <summary>创建 Translatable 类型 FText</summary>
        public static FText Translatable(string key, string sourceString, string namespaceName = "Default") =>
            new(FTextMarkupType.Translatable, key, namespaceName, sourceString);

        /// <summary>创建 Literal 类型 FText（不翻译的纯文本）</summary>
        public static FText Literal(string text) =>
            new(FTextMarkupType.Literal, "_literal", "", text);

        /// <summary>创建 Plural 类型 FText</summary>
        public static FText Plural(string key, string sourceString, int count, string namespaceName = "Default") =>
            new FText(FTextMarkupType.Plural, key, namespaceName, sourceString)
                .With("count", count);

        /// <summary>创建 Gender 类型 FText</summary>
        public static FText Gender(string key, string sourceString, string gender, string namespaceName = "Default") =>
            new FText(FTextMarkupType.Gender, key, namespaceName, sourceString)
                .With("gender", gender);

        /// <summary>创建 Duration 类型 FText</summary>
        public static FText Duration(float seconds, bool showHours = false) =>
            new FText(FTextMarkupType.Duration, "_duration", "", "")
                .With("seconds", seconds)
                .With("showHours", showHours ? 1 : 0);

        public override string ToString() => Format();
    }

    /// <summary>
    /// FTextArgument：格式化参数。支持字符串、数字、FText 嵌套和富文本。
    /// </summary>
    public sealed class FTextArgument
    {
        /// <summary>参数名称</summary>
        public string Name { get; }

        /// <summary>参数值</summary>
        public object? Value { get; }

        /// <summary>数字格式字符串（可选）</summary>
        public string? NumberFormat { get; set; }

        public FTextArgument(string name, object? value)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }

        /// <summary>获取值的字符串表示</summary>
        public string GetValueString()
        {
            return Value switch
            {
                null => "",
                string s => s,
                int i => NumberFormat != null ? i.ToString(NumberFormat, CultureInfo.InvariantCulture)
                    : i.ToString(CultureInfo.InvariantCulture),
                float f => NumberFormat != null ? f.ToString(NumberFormat, CultureInfo.InvariantCulture)
                    : f.ToString("F2", CultureInfo.InvariantCulture),
                double d => NumberFormat != null ? d.ToString(NumberFormat, CultureInfo.InvariantCulture)
                    : d.ToString("F2", CultureInfo.InvariantCulture),
                FText ft => ft.Format(),
                bool b => b ? "true" : "false",
                _ => Value.ToString() ?? ""
            };
        }
    }

    /// <summary>
    /// FTextFormat：格式化参数处理器。处理 {name}、数字格式化、复数和性别变体。
    /// </summary>
    public sealed class FTextFormatter
    {
        private readonly Dictionary<string, object?> _globalArgs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前语言代码（影响复数和数字格式）</summary>
        public string LanguageCode { get; set; } = "en";

        /// <summary>当前语言文化信息</summary>
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

        /// <summary>设置全局参数（适用于所有格式化调用）</summary>
        public FTextFormatter WithGlobalArg(string name, object? value)
        {
            _globalArgs[name] = value;
            return this;
        }

        /// <summary>格式化 FText</summary>
        public string Format(FText text)
        {
            if (text == null) return "";
            if (text.MarkupType == FTextMarkupType.Literal) return text.SourceString;

            string result = text.SourceString;

            // 先替换文本自身的参数
            foreach (var arg in text.Arguments)
            {
                result = ReplaceArgument(result, arg);
            }

            // 再替换全局参数
            foreach (var kvp in _globalArgs)
            {
                string placeholder = $"{{{kvp.Key}}}";
                string value = FormatValue(kvp.Value);
                result = result.Replace(placeholder, value);
            }

            // 处理复数形式
            if (text.MarkupType == FTextMarkupType.Plural && text.Arguments.Count > 0)
            {
                var countArg = text.Arguments.FirstOrDefault(a => a.Name == "count");
                if (countArg != null && countArg.Value is int count)
                {
                    result = ApplyPluralForm(result, count);
                }
            }

            // 处理时长
            if (text.MarkupType == FTextMarkupType.Duration)
            {
                var secArg = text.Arguments.FirstOrDefault(a => a.Name == "seconds");
                if (secArg != null && secArg.Value is float seconds)
                {
                    result = FormatDuration(seconds);
                }
            }

            return result;
        }

        /// <summary>格式化简单字符串模板</summary>
        public string Format(string template, params (string name, object? value)[] args)
        {
            string result = template;
            foreach (var (name, value) in args)
            {
                string placeholder = $"{{{name}}}";
                result = result.Replace(placeholder, FormatValue(value));
            }
            return result;
        }

        private string ReplaceArgument(string text, FTextArgument arg)
        {
            string placeholder = $"{{{arg.Name}}}";
            return text.Replace(placeholder, arg.GetValueString());
        }

        private string FormatValue(object? value)
        {
            return value switch
            {
                null => "",
                string s => s,
                int i => i.ToString(Culture),
                float f => f.ToString("F2", Culture),
                double d => d.ToString("F2", Culture),
                FText ft => Format(ft),
                bool b => b ? "true" : "false",
                _ => value.ToString() ?? ""
            };
        }

        private string ApplyPluralForm(string text, int count)
        {
            // 简化版复数形式：
            // 检测 {count|one:xxx|other:xxx} 格式
            var match = Regex.Match(text, @"\{count\|([^}]+)\}");
            if (!match.Success) return text;

            string forms = match.Groups[1].Value;
            var parts = forms.Split('|');
            string selectedForm;

            if (count == 1)
                selectedForm = parts.FirstOrDefault(p => p.StartsWith("one:"))?.Substring(4)
                    ?? parts.FirstOrDefault(p => p.StartsWith("other:"))?.Substring(6) ?? text;
            else
                selectedForm = parts.FirstOrDefault(p => p.StartsWith("other:"))?.Substring(6)
                    ?? parts.FirstOrDefault(p => p.StartsWith("one:"))?.Substring(4) ?? text;

            return text.Substring(0, match.Index) + selectedForm + text.Substring(match.Index + match.Length);
        }

        private string FormatDuration(float seconds)
        {
            int totalSeconds = (int)Math.Abs(seconds);
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int secs = totalSeconds % 60;

            if (hours > 0)
                return $"{hours:D2}:{minutes:D2}:{secs:D2}";
            return $"{minutes:D2}:{secs:D2}";
        }
    }

    /// <summary>
    /// FTextFromString：从纯字符串创建的非本地化 FText。
    /// </summary>
    public static class FTextFromString
    {
        /// <summary>创建纯字符串 FText（不翻译）</summary>
        public static FText Create(string text) => FText.Literal(text);

        /// <summary>创建带格式化参数的纯字符串 FText</summary>
        public static FText Create(string template, params (string name, object? value)[] args)
        {
            var text = FText.Literal(template);
            foreach (var (name, value) in args)
                text.With(name, value.ToString() ?? "");
            return text;
        }
    }

    /// <summary>
    /// FTextFromTable：从字符串表查找创建的 FText。
    /// 在运行时解析时通过 Core.Localization 查找实际翻译。
    /// </summary>
    public static class FTextFromTable
    {
        /// <summary>创建从字符串表查找的 FText</summary>
        public static FText Create(string key, string namespaceName = "Default")
        {
            return new FText(FTextMarkupType.Translatable, key, namespaceName, $"{{{key}}}");
        }

        /// <summary>创建从字符串表查找的 FText（带参数）</summary>
        public static FText Create(string key, string namespaceName, params (string name, object? value)[] args)
        {
            var text = new FText(FTextMarkupType.Translatable, key, namespaceName, $"{{{key}}}");
            foreach (var (name, value) in args)
                text.With(name, value?.ToString() ?? "");
            return text;
        }
    }
}
