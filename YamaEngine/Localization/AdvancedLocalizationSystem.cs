using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace YamaEngine.Localization
{
    // ======================== 文本方向 ========================

    public enum TextDirection
    {
        LTR,
        RTL
    }

    // ======================== 复数规则 ========================

    public enum PluralCategory
    {
        Zero,
        One,
        Two,
        Few,
        Many,
        Other
    }

    /// <summary>
    /// ICU 兼容的复数规则。支持中文(仅Other)、英文(One/Other)、阿拉伯语(Zero/One/Two/Few/Many/Other)等。
    /// </summary>
    public static class PluralRules
    {
        public delegate PluralCategory PluralRuleDelegate(decimal n, int digits);

        private static readonly Dictionary<string, PluralRuleDelegate> Rules = new Dictionary<string, PluralRuleDelegate>
        {
            ["zh"] = ChineseRule,
            ["zh-CN"] = ChineseRule,
            ["zh-TW"] = ChineseRule,
            ["ja"] = ChineseRule,
            ["ko"] = ChineseRule,
            ["en"] = EnglishRule,
            ["de"] = EnglishRule,
            ["es"] = EnglishRule,
            ["fr"] = FrenchRule,
            ["ar"] = ArabicRule,
            ["ru"] = RussianRule,
            ["pl"] = PolishRule,
        };

        public static PluralCategory Select(string culture, decimal n)
        {
            int digits = GetDecimalDigits(n);
            if (Rules.TryGetValue(culture, out var rule))
                return rule(n, digits);
            return EnglishRule(n, digits);
        }

        private static int GetDecimalDigits(decimal n)
        {
            string s = n.ToString(CultureInfo.InvariantCulture);
            int idx = s.IndexOf('.');
            return idx >= 0 ? s.Length - idx - 1 : 0;
        }

        public static PluralCategory ChineseRule(decimal n, int digits)
        {
            return PluralCategory.Other;
        }

        public static PluralCategory EnglishRule(decimal n, int digits)
        {
            if (digits != 0) return PluralCategory.Other;
            return n == 1 ? PluralCategory.One : PluralCategory.Other;
        }

        public static PluralCategory FrenchRule(decimal n, int digits)
        {
            if (digits != 0) return PluralCategory.Other;
            return (n == 0 || n == 1) ? PluralCategory.One : PluralCategory.Other;
        }

        public static PluralCategory ArabicRule(decimal n, int digits)
        {
            if (digits != 0) return PluralCategory.Other;
            int i = (int)n;
            int mod100 = i % 100;
            if (n == 0) return PluralCategory.Zero;
            if (n == 1) return PluralCategory.One;
            if (n == 2) return PluralCategory.Two;
            if (mod100 >= 3 && mod100 <= 10) return PluralCategory.Few;
            if (mod100 >= 11 && mod100 <= 99) return PluralCategory.Many;
            return PluralCategory.Other;
        }

        public static PluralCategory RussianRule(decimal n, int digits)
        {
            if (digits != 0) return PluralCategory.Other;
            int mod10 = (int)(n % 10);
            int mod100 = (int)(n % 100);
            if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return PluralCategory.Few;
            return PluralCategory.Other;
        }

        public static PluralCategory PolishRule(decimal n, int digits)
        {
            if (digits != 0) return PluralCategory.Other;
            int i = (int)n;
            if (i == 1) return PluralCategory.One;
            int mod10 = i % 10;
            int mod100 = i % 100;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return PluralCategory.Few;
            return PluralCategory.Other;
        }
    }

    // ======================== 格式化字符串 ========================

    /// <summary>
    /// ICU 兼容的格式化字符串解析器。支持：{0}, {name}, {count:plural}
    /// </summary>
    public static class FormatString
    {
        private static readonly Regex PlaceholderRegex = new Regex(
            @"\{(?<name>[A-Za-z_]\w*)(?::(?<format>[^}]+))?\}",
            RegexOptions.Compiled);

        public static string Format(string pattern, params object[] args)
        {
            if (string.IsNullOrEmpty(pattern)) return "";
            var dict = new Dictionary<string, object>();
            for (int i = 0; i < args.Length; i++)
                dict[i.ToString()] = args[i];
            return FormatNamed(pattern, dict);
        }

        public static string FormatNamed(string pattern, IReadOnlyDictionary<string, object> values, string culture = "en")
        {
            if (string.IsNullOrEmpty(pattern)) return "";
            var sb = new StringBuilder();
            int lastIndex = 0;
            foreach (Match m in PlaceholderRegex.Matches(pattern))
            {
                sb.Append(pattern, lastIndex, m.Index - lastIndex);
                string name = m.Groups["name"].Value;
                string format = m.Groups["format"].Success ? m.Groups["format"].Value : null;
                string replacement = ResolvePlaceholder(name, format, values, culture);
                sb.Append(replacement);
                lastIndex = m.Index + m.Length;
            }
            sb.Append(pattern, lastIndex, pattern.Length - lastIndex);
            return sb.ToString();
        }

        private static string ResolvePlaceholder(string name, string format, IReadOnlyDictionary<string, object> values, string culture)
        {
            if (!values.TryGetValue(name, out var value))
                return $"{{{name}}}";

            if (format == null)
                return value?.ToString() ?? "";

            if (format.StartsWith("plural", StringComparison.OrdinalIgnoreCase))
            {
                if (value is decimal dec)
                    return ResolvePlural(dec, format, culture);
                if (value is int i)
                    return ResolvePlural(i, format, culture);
                if (value is long l)
                    return ResolvePlural(l, format, culture);
                if (value is float f)
                    return ResolvePlural((decimal)f, format, culture);
                if (value is double d)
                    return ResolvePlural((decimal)d, format, culture);
            }

            if (value is IFormattable formattable)
                return formattable.ToString(format, CultureInfo.GetCultureInfo(culture));

            return value?.ToString() ?? "";
        }

        private static string ResolvePlural(decimal count, string format, string culture)
        {
            var parts = format.Split('|');
            var map = new Dictionary<string, string>();
            foreach (var part in parts)
            {
                int colon = part.IndexOf(':');
                if (colon > 0)
                {
                    string key = part.Substring(0, colon).Trim().ToLowerInvariant();
                    string val = part.Substring(colon + 1).Trim();
                    map[key] = val;
                }
            }
            var category = PluralRules.Select(culture, count);
            string catName = category.ToString().ToLowerInvariant();
            if (map.TryGetValue(catName, out var result))
                return result;
            if (map.TryGetValue("other", out var other))
                return other;
            return count.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ======================== 翻译条目与内存 ========================

    public enum TranslationStatus
    {
        Untranslated,
        Draft,
        Reviewed,
        Final
    }

    public class TranslationEntry
    {
        public string Key { get; set; }
        public string SourceText { get; set; }
        public string TranslatedText { get; set; }
        public string Context { get; set; }
        public TranslationStatus Status { get; set; }
        public string Comment { get; set; }
        public DateTime LastModified { get; set; }
        public string Author { get; set; }
    }

    /// <summary>
    /// 上下文感知翻译内存。支持模糊匹配与状态追踪。
    /// </summary>

    // ======================== 字体回退 ========================

    public class FontFallbackChain
    {
        public List<string> FontNames { get; set; } = new List<string>();
        public string ResolveGlyph(char c)
        {
            foreach (var font in FontNames)
            {
                // In real engine, check font glyph coverage
                return font;
            }
            return FontNames.Count > 0 ? FontNames[FontNames.Count - 1] : "Default";
        }
    }

    // ======================== 文化信息 ========================

    public class LocaleCultureInfo
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string NativeName { get; set; }
        public TextDirection Direction { get; set; }
        public string DateFormat { get; set; } = "yyyy-MM-dd";
        public string TimeFormat { get; set; } = "HH:mm:ss";
        public string NumberDecimalSeparator { get; set; } = ".";
        public string NumberGroupSeparator { get; set; } = ",";
        public string CurrencySymbol { get; set; } = "$";
        public string CurrencyFormat { get; set; } = "${0:N2}";
        public FontFallbackChain Fonts { get; set; } = new FontFallbackChain();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public string FormatDateTime(DateTime dt) => dt.ToString($"{DateFormat} {TimeFormat}", CultureInfo.InvariantCulture);
        public string FormatNumber(decimal n) => n.ToString("N", CultureInfo.InvariantCulture).Replace(".", NumberDecimalSeparator).Replace(",", NumberGroupSeparator);
        public string FormatCurrency(decimal n) => string.Format(CultureInfo.InvariantCulture, CurrencyFormat, n).Replace("$", CurrencySymbol);
    }

    // ======================== 区域数据 ========================


    public class LocaleData
    {
        public string Locale { get; set; }
        public StringTable Strings { get; set; } = new StringTable();
        public TranslationMemory Memory { get; set; } = new TranslationMemory();
        public LocaleCultureInfo Culture { get; set; } = new LocaleCultureInfo();

        public string GetString(string key, string fallback = null)
        {
            var text = Strings[key];
            if (!string.IsNullOrEmpty(text))
                return text;
            var mem = Memory.Translate(key, fallback);
            return mem ?? fallback ?? key;
        }

        public string GetFormattedString(string key, IReadOnlyDictionary<string, object> args, string fallback = null)
        {
            var pattern = GetString(key, fallback);
            return FormatString.FormatNamed(pattern, args, Locale);
        }
    }

    // ======================== 资产导入导出 ========================

    public enum ImportFormat
    {
        CSV,
        XLIFF,
        PO
    }

    /// <summary>
    /// CSV/XLIFF/GetText(.po) 导入导出器。
    /// </summary>
    public static class LocaleAssetImporter
    {
        public static LocaleData ImportCSV(string filePath, string locale, string keyColumn = "Key", string valueColumn = "Value", string contextColumn = "Context")
        {
            var data = new LocaleData { Locale = locale };
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            if (lines.Length == 0) return data;

            var headers = ParseCsvLine(lines[0]);
            int keyIdx = headers.IndexOf(keyColumn);
            int valIdx = headers.IndexOf(valueColumn);
            int ctxIdx = contextColumn != null ? headers.IndexOf(contextColumn) : -1;
            if (keyIdx < 0 || valIdx < 0) return data;

            for (int i = 1; i < lines.Length; i++)
            {
                var parts = ParseCsvLine(lines[i]);
                if (parts.Count <= Math.Max(keyIdx, valIdx)) continue;
                var key = parts[keyIdx];
                var value = parts[valIdx];
                var context = ctxIdx >= 0 && ctxIdx < parts.Count ? parts[ctxIdx] : null;
                data.Strings[key] = value;
                data.Memory.AddOrUpdate(new TranslationEntry
                {
                    Key = key,
                    SourceText = key,
                    TranslatedText = value,
                    Context = context,
                    Status = TranslationStatus.Final
                });
            }
            return data;
        }

        public static void ExportCSV(LocaleData data, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Key,Value,Context,Status");
            foreach (var kv in data.Strings.GetAll())
            {
                var entry = data.Memory.GetByKey(kv.Key);
                var ctx = EscapeCsv(entry?.Context ?? "");
                var status = entry?.Status.ToString() ?? "Final";
                sb.AppendLine($"{EscapeCsv(kv.Key)},{EscapeCsv(kv.Value)},{ctx},{status}");
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        public static LocaleData ImportXLIFF(string filePath, string locale)
        {
            var data = new LocaleData { Locale = locale };
            var doc = XDocument.Load(filePath);
            var ns = doc.Root.GetDefaultNamespace();
            foreach (var file in doc.Root.Elements(ns + "file"))
            {
                foreach (var unit in file.Descendants(ns + "trans-unit"))
                {
                    string id = unit.Attribute("id")?.Value;
                    string source = unit.Element(ns + "source")?.Value;
                    string target = unit.Element(ns + "target")?.Value;
                    string note = unit.Element(ns + "note")?.Value;
                    if (string.IsNullOrEmpty(id)) continue;
                    data.Strings[id] = target ?? source ?? id;
                    data.Memory.AddOrUpdate(new TranslationEntry
                    {
                        Key = id,
                        SourceText = source,
                        TranslatedText = target,
                        Context = note,
                        Status = string.IsNullOrEmpty(target) ? TranslationStatus.Untranslated : TranslationStatus.Final
                    });
                }
            }
            return data;
        }

        public static void ExportXLIFF(LocaleData data, string filePath, string sourceLocale = "en")
        {
            var ns = "urn:oasis:names:tc:xliff:document:1.2";
            var root = new XElement(XName.Get("xliff", ns),
                new XAttribute("version", "1.2"),
                new XElement(XName.Get("file", ns),
                    new XAttribute("source-language", sourceLocale),
                    new XAttribute("target-language", data.Locale),
                    new XAttribute("datatype", "plaintext"),
                    new XAttribute("original", "engine-strings"),
                    new XElement(XName.Get("body", ns),
                        data.Memory.GetAll().Select(e => new XElement(XName.Get("trans-unit", ns),
                            new XAttribute("id", e.Key),
                            new XElement(XName.Get("source", ns), e.SourceText),
                            new XElement(XName.Get("target", ns), e.TranslatedText ?? ""),
                            string.IsNullOrEmpty(e.Comment) ? null : new XElement(XName.Get("note", ns), e.Comment)
                        ))
                    )
                )
            );
            new XDocument(root).Save(filePath);
        }

        public static LocaleData ImportPO(string filePath, string locale)
        {
            var data = new LocaleData { Locale = locale };
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            string currentMsgId = null;
            string currentMsgStr = null;
            string currentContext = null;
            var sb = new StringBuilder();
            bool inMsgId = false;
            bool inMsgStr = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("msgctxt "))
                {
                    currentContext = UnescapePoString(line.Substring(8).Trim().Trim('"'));
                }
                else if (line.StartsWith("msgid "))
                {
                    if (currentMsgId != null && currentMsgStr != null)
                    {
                        data.Strings[currentMsgId] = currentMsgStr;
                        data.Memory.AddOrUpdate(new TranslationEntry
                        {
                            Key = currentMsgId,
                            SourceText = currentMsgId,
                            TranslatedText = currentMsgStr,
                            Context = currentContext,
                            Status = string.IsNullOrEmpty(currentMsgStr) ? TranslationStatus.Untranslated : TranslationStatus.Final
                        });
                    }
                    currentMsgId = UnescapePoString(line.Substring(6).Trim().Trim('"'));
                    currentMsgStr = null;
                    currentContext = null;
                    sb.Clear();
                    inMsgId = false;
                    inMsgStr = false;
                }
                else if (line.StartsWith("msgstr "))
                {
                    sb.Clear();
                    sb.Append(UnescapePoString(line.Substring(7).Trim().Trim('"')));
                    inMsgStr = true;
                    inMsgId = false;
                }
                else if (line.StartsWith("\"") && inMsgStr)
                {
                    sb.Append(UnescapePoString(line.Trim('"')));
                }
                else if (string.IsNullOrEmpty(line) && currentMsgId != null && inMsgStr)
                {
                    currentMsgStr = sb.ToString();
                }
            }
            if (currentMsgId != null && inMsgStr)
            {
                currentMsgStr = sb.ToString();
                data.Strings[currentMsgId] = currentMsgStr;
                data.Memory.AddOrUpdate(new TranslationEntry
                {
                    Key = currentMsgId,
                    SourceText = currentMsgId,
                    TranslatedText = currentMsgStr,
                    Context = currentContext,
                    Status = string.IsNullOrEmpty(currentMsgStr) ? TranslationStatus.Untranslated : TranslationStatus.Final
                });
            }
            return data;
        }

        public static void ExportPO(LocaleData data, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"msgid \"\"");
            sb.AppendLine($"msgstr \"\"");
            sb.AppendLine($"\"Content-Type: text/plain; charset=UTF-8\\n\"");
            sb.AppendLine($"\"Language: {data.Locale}\\n\"");
            sb.AppendLine();
            foreach (var entry in data.Memory.GetAll())
            {
                if (!string.IsNullOrEmpty(entry.Context))
                    sb.AppendLine($"msgctxt \"{EscapePoString(entry.Context)}\"");
                sb.AppendLine($"msgid \"{EscapePoString(entry.SourceText ?? entry.Key)}\"");
                sb.AppendLine($"msgstr \"{EscapePoString(entry.TranslatedText ?? "")}\"");
                sb.AppendLine();
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private static List<string> ParseCsvLine(string line)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            parts.Add(sb.ToString());
            return parts;
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string EscapePoString(string s)
        {
            return s?.Replace("\\", "\\\\")
                     .Replace("\"", "\\\"")
                     .Replace("\n", "\\n")
                     .Replace("\r", "\\r")
                     .Replace("\t", "\\t") ?? "";
        }

        private static string UnescapePoString(string s)
        {
            return s?.Replace("\\n", "\n")
                     .Replace("\\r", "\r")
                     .Replace("\\t", "\t")
                     .Replace("\\\\", "\\")
                     .Replace("\\\"", "\"") ?? "";
        }
    }

    // ======================== 本地化管理器 ========================


    // ======================== 实时预览 ========================

    /// <summary>
    /// 编辑器实时语言切换预览系统。
    /// </summary>
    public class LocalizationLivePreview
    {
        private readonly LocalizationManager _manager;
        private string _previewLocale;
        private bool _isPreviewActive;
        private string _originalLocale;

        public LocalizationLivePreview(LocalizationManager manager)
        {
            _manager = manager;
        }

        public bool IsPreviewActive => _isPreviewActive;
        public string PreviewLocale => _previewLocale;

        public void BeginPreview(string locale)
        {
            if (_isPreviewActive) EndPreview();
            _originalLocale = _manager.CurrentLocale.Tag;
            _previewLocale = locale;
            _isPreviewActive = true;
            _manager.SetLocale(locale);
        }

        public void EndPreview()
        {
            if (!_isPreviewActive) return;
            _isPreviewActive = false;
            if (!string.IsNullOrEmpty(_originalLocale))
                _manager.SetLocale(_originalLocale);
            _previewLocale = null;
            _originalLocale = null;
        }

        public void TogglePreview(string locale)
        {
            if (_isPreviewActive && _previewLocale == locale)
                EndPreview();
            else
                BeginPreview(locale);
        }

        public void RefreshPreview()
        {
            if (_isPreviewActive && !string.IsNullOrEmpty(_previewLocale))
            {
                _manager.SetLocale(_previewLocale);
            }
        }
    }
}
