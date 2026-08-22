using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShanyuEngine.Localization;

namespace ShanyuEngine.Core
{
    // ══════════════════════════════════════════════════════════════════
    // 本地化系统（LocalizationSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现山雨引擎的多语言本地化子系统，包含以下能力：
    //   1. StringTable：字符串表（按命名空间组织多语言文本）
    //   2. Culture / Locale 管理（en-US / zh-CN / ja-JP / ko-KR / fr-FR 等）
    //   3. 文本查找：通过 "命名空间.键" 查找（如 "UI.MainMenu.StartGame"）
    //   4. 富文本格式化：变量插值 {name}、复数形式 {count|plural}、性别变化 {gender|m/f}
    //   5. 字体回退链（不同语言使用不同字体）
    //   6. 运行时语言切换（带事件通知）
    //   7. 字符串表加载（JSON / CSV / PO 三种格式）
    //   8. 热重载本地化（文件变更后重新加载）
    //   9. 缺失字符串警告与默认值回退
    // 通过单例 Localization 全局访问，可与 UI 系统、资产系统对接。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 引擎支持的区域设置（Locale）。每个 Locale 对应一种语言 / 地区组合，
    /// 携带 BCP-47 标签、显示名称与默认字体族信息。
    /// </summary>
    public sealed class LocaleInfo
    {
        /// <summary>BCP-47 语言标签，如 "en-US"、"zh-CN"</summary>
        public string Tag { get; }
        /// <summary>英文显示名（如 "Chinese (Simplified)"）</summary>
        public string EnglishName { get; }
        /// <summary>本地化显示名（如 "简体中文"）</summary>
        public string NativeName { get; }
        /// <summary>ISO 639-1 两字母语言码（如 "zh"、"en"）</summary>
        public string LanguageCode { get; }
        /// <summary>文字方向（从左到右 / 从右到左）</summary>
        public TextDirection Direction { get; }
        /// <summary>默认字体族（该语言优先使用的字体）</summary>
        public string DefaultFontFamily { get; }

        public LocaleInfo(string tag, string englishName, string nativeName,
                          string langCode, TextDirection direction, string defaultFont)
        {
            Tag = tag;
            EnglishName = englishName;
            NativeName = nativeName;
            LanguageCode = langCode;
            Direction = direction;
            DefaultFontFamily = defaultFont;
        }

        public override string ToString() => $"{Tag} ({NativeName})";
    }

    /// <summary>文字方向</summary>
    public enum TextDirection
    {
        /// <summary>从左到右（拉丁语系、中文等）</summary>
        LeftToRight,
        /// <summary>从右到左（阿拉伯语、希伯来语等）</summary>
        RightToLeft
    }

    /// <summary>
    /// 区域设置预设。提供引擎内置支持的常用 Locale 定义，
    /// 可直接通过 <see cref="LocalePresets.All"/> 获取全部预设。
    /// </summary>
    public static class LocalePresets
    {
        /// <summary>英语（美国）</summary>
        public static readonly LocaleInfo EnglishUS =
            new("en-US", "English (United States)", "English", "en", TextDirection.LeftToRight, "Roboto");
        /// <summary>简体中文</summary>
        public static readonly LocaleInfo ChineseSimplified =
            new("zh-CN", "Chinese (Simplified)", "简体中文", "zh", TextDirection.LeftToRight, "NotoSansSC");
        /// <summary>繁体中文</summary>
        public static readonly LocaleInfo ChineseTraditional =
            new("zh-TW", "Chinese (Traditional)", "繁體中文", "zh", TextDirection.LeftToRight, "NotoSansTC");
        /// <summary>日语</summary>
        public static readonly LocaleInfo Japanese =
            new("ja-JP", "Japanese", "日本語", "ja", TextDirection.LeftToRight, "NotoSansJP");
        /// <summary>韩语</summary>
        public static readonly LocaleInfo Korean =
            new("ko-KR", "Korean", "한국어", "ko", TextDirection.LeftToRight, "NotoSansKR");
        /// <summary>法语（法国）</summary>
        public static readonly LocaleInfo French =
            new("fr-FR", "French (France)", "Français", "fr", TextDirection.LeftToRight, "Roboto");
        /// <summary>德语（德国）</summary>
        public static readonly LocaleInfo German =
            new("de-DE", "German (Germany)", "Deutsch", "de", TextDirection.LeftToRight, "Roboto");
        /// <summary>西班牙语（西班牙）</summary>
        public static readonly LocaleInfo Spanish =
            new("es-ES", "Spanish (Spain)", "Español", "es", TextDirection.LeftToRight, "Roboto");
        /// <summary>葡萄牙语（巴西）</summary>
        public static readonly LocaleInfo PortugueseBR =
            new("pt-BR", "Portuguese (Brazil)", "Português", "pt", TextDirection.LeftToRight, "Roboto");
        /// <summary>俄语</summary>
        public static readonly LocaleInfo Russian =
            new("ru-RU", "Russian", "Русский", "ru", TextDirection.LeftToRight, "Roboto");
        /// <summary>阿拉伯语</summary>
        public static readonly LocaleInfo Arabic =
            new("ar-SA", "Arabic (Saudi Arabia)", "العربية", "ar", TextDirection.RightToLeft, "NotoSansArabic");

        /// <summary>所有内置预设 Locale</summary>
        public static readonly IReadOnlyList<LocaleInfo> All = new[]
        {
            EnglishUS, ChineseSimplified, ChineseTraditional, Japanese, Korean,
            French, German, Spanish, PortugueseBR, Russian, Arabic
        };

        /// <summary>按标签查找预设 Locale</summary>
        public static LocaleInfo? Find(string tag) =>
            All.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 单条本地化字符串条目。除主文本外，可携带复数形式、
    /// 性别变体与格式化参数元信息。
    /// </summary>
    public sealed class LocalizedStringEntry
    {
        /// <summary>完整键名（命名空间.键）</summary>
        public string Key { get; set; } = "";
        /// <summary>主文本（单数 / 默认形式）</summary>
        public string Text { get; set; } = "";
        /// <summary>复数形式文本（可选）。索引 0=零、1=一、2=少数、3=多数、4=其它</summary>
        public string[]? Plurals { get; set; }
        /// <summary>男性变体文本（可选）</summary>
        public string? MasculineText { get; set; }
        /// <summary>女性变体文本（可选）</summary>
        public string? FeminineText { get; set; }
        /// <summary>中性变体文本（可选）</summary>
        public string? NeutralText { get; set; }
        /// <summary>注释（译者备注，不显示给玩家）</summary>
        public string? Comment { get; set; }
    }

    /// <summary>
    /// 字符串表。保存某个 Locale 下的所有本地化条目，
    /// 以 "命名空间.键" 为索引。一个 StringTable 对应一种语言。
    /// </summary>

    /// <summary>
    /// 字体回退链。为每种语言定义一组按优先级排列的字体族，
    /// 当首选字体缺少某字符的字形时，依次尝试后续字体。
    /// 这对混合 CJK + 拉丁文本的场景尤为重要。
    /// </summary>
    public sealed class FontFallbackChain
    {
        private readonly Dictionary<string, List<string>> _chains = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>为指定语言设置字体回退链（按优先级从高到低）</summary>
        public void Set(string languageCode, params string[] fontFamilies)
        {
            if (string.IsNullOrEmpty(languageCode)) return;
            _chains[languageCode] = new List<string>(fontFamilies);
        }

        /// <summary>获取指定语言的字体回退链（返回副本）</summary>
        public IReadOnlyList<string> Get(string languageCode)
        {
            return _chains.TryGetValue(languageCode ?? "", out var list)
                ? list
                : Array.Empty<string>();
        }

        /// <summary>是否存在该语言的回退链</summary>
        public bool Has(string languageCode) =>
            _chains.ContainsKey(languageCode ?? "");

        /// <summary>获取首个字体（首选字体），无则返回 null</summary>
        public string? GetPrimary(string languageCode) =>
            _chains.TryGetValue(languageCode ?? "", out var list) && list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// 文本格式化参数。用于富文本插值、复数选择与性别变体选择。
    /// 通过字典传入命名参数，由 <see cref="Localization.Format"/> 解析。
    /// </summary>
    public sealed class FormatArguments
    {
        private readonly Dictionary<string, object?> _args = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>添加一个命名参数</summary>
        public FormatArguments With(string name, object? value)
        {
            _args[name] = value;
            return this;
        }

        /// <summary>设置用于复数选择的数量参数</summary>
        public FormatArguments WithCount(int count) => With("count", count);

        /// <summary>设置用于性别变体的性别参数（"m"/"f"/"n"）</summary>
        public FormatArguments WithGender(string gender) => With("gender", gender);

        /// <summary>获取参数值</summary>
        public object? Get(string name) =>
            _args.TryGetValue(name, out var v) ? v : null;

        /// <summary>尝试获取整数值</summary>
        public bool TryGetInt(string name, out int value)
        {
            if (_args.TryGetValue(name, out var v) && v != null)
            {
                if (v is int i) { value = i; return true; }
                if (int.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
            }
            value = 0;
            return false;
        }

        /// <summary>是否包含某参数</summary>
        public bool Has(string name) => _args.ContainsKey(name);

        /// <summary>所有参数名</summary>
        public IEnumerable<string> Names => _args.Keys;

        /// <summary>空参数</summary>
        public static FormatArguments Empty => new();
    }

    /// <summary>
    /// 缺失字符串记录。当查找的键在当前语言表中不存在时生成，
    /// 用于开发期发现未翻译的条目。
    /// </summary>
    public readonly struct MissingStringRecord
    {
        /// <summary>缺失的键</summary>
        public readonly string Key;
        /// <summary>查找时的 Locale</summary>
        public readonly string LocaleTag;
        /// <summary>查找时间戳</summary>
        public readonly DateTime Timestamp;

        public MissingStringRecord(string key, string localeTag)
        {
            Key = key;
            LocaleTag = localeTag;
            Timestamp = DateTime.UtcNow;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 字符串表加载器（JSON / CSV / PO）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 字符串表加载器。支持从 JSON、CSV、PO 三种格式加载 <see cref="StringTable"/>。
    /// 各格式约定：
    ///   - JSON：{ "key": "text", ... } 或带元数据的对象数组
    ///   - CSV ：key,text[,plural_one,plural_other]
    ///   - PO  ：标准 GNU gettext .po 文件（msgid/msgstr）
    /// </summary>
    public static class StringTableLoader
    {
        /// <summary>从 JSON 字符串加载。格式：{ "UI.Start": "开始", "UI.Exit": "退出" }</summary>
        public static StringTable FromJson(string json, LocaleInfo locale)
        {
            var table = new StringTable(locale);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    table.Set(prop.Name, prop.Value.GetString() ?? "");
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    // 带元数据的条目
                    var entry = new LocalizedStringEntry { Key = prop.Name };
                    if (prop.Value.TryGetProperty("text", out var textEl))
                        entry.Text = textEl.GetString() ?? "";
                    if (prop.Value.TryGetProperty("comment", out var cEl))
                        entry.Comment = cEl.GetString();
                    if (prop.Value.TryGetProperty("plural", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in pEl.EnumerateArray()) list.Add(item.GetString() ?? "");
                        entry.Plurals = list.ToArray();
                    }
                    table.Set(entry);
                }
            }
            return table;
        }

        /// <summary>从 JSON 文件加载</summary>
        public static StringTable FromJsonFile(string path, LocaleInfo locale) =>
            FromJson(File.ReadAllText(path, Encoding.UTF8), locale);

        /// <summary>
        /// 从 CSV 字符串加载。每行格式：key,text
        /// 可选复数列：key,text,plural_one,plural_other
        /// 首行若为表头（以 # 开头或含 "key"）则跳过。
        /// </summary>
        public static StringTable FromCsv(string csv, LocaleInfo locale)
        {
            var table = new StringTable(locale);
            var lines = csv.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                // 跳过表头
                if (i == 0 && line.StartsWith("key,", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = ParseCsvLine(line);
                if (parts.Length < 2) continue;

                var entry = new LocalizedStringEntry { Key = parts[0], Text = parts[1] };
                if (parts.Length > 2)
                {
                    var plurals = new List<string>();
                    for (int j = 2; j < parts.Length; j++) plurals.Add(parts[j]);
                    entry.Plurals = plurals.ToArray();
                }
                table.Set(entry);
            }
            return table;
        }

        /// <summary>从 CSV 文件加载</summary>
        public static StringTable FromCsvFile(string path, LocaleInfo locale) =>
            FromCsv(File.ReadAllText(path, Encoding.UTF8), locale);

        // 简易 CSV 行解析（支持双引号包裹与转义）
        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && !inQuotes) { inQuotes = true; continue; }
                if (c == '"' && inQuotes)
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = false; }
                    continue;
                }
                if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        /// <summary>
        /// 从 GNU gettext .po 格式字符串加载。
        /// 解析 msgid / msgstr 对，支持多行 msgstr。
        /// </summary>
        public static StringTable FromPo(string po, LocaleInfo locale)
        {
            var table = new StringTable(locale);
            var lines = po.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            string? currentKey = null;
            var currentText = new StringBuilder();
            bool readingMsgStr = false;

            void FlushEntry()
            {
                if (currentKey != null && currentText.Length > 0)
                    table.Set(currentKey, currentText.ToString());
                currentKey = null;
                currentText.Clear();
                readingMsgStr = false;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) { FlushEntry(); continue; }
                if (line.StartsWith("#")) continue; // 注释 / 元数据

                if (line.StartsWith("msgid ", StringComparison.OrdinalIgnoreCase))
                {
                    FlushEntry();
                    currentKey = UnquotePo(line.Substring(6));
                }
                else if (line.StartsWith("msgstr ", StringComparison.OrdinalIgnoreCase))
                {
                    readingMsgStr = true;
                    currentText.Append(UnquotePo(line.Substring(7)));
                }
                else if (line.StartsWith("\"") && readingMsgStr)
                {
                    currentText.Append(UnquotePo(line));
                }
            }
            FlushEntry();
            return table;
        }

        /// <summary>从 .po 文件加载</summary>
        public static StringTable FromPoFile(string path, LocaleInfo locale) =>
            FromPo(File.ReadAllText(path, Encoding.UTF8), locale);

        // 去除 PO 字符串两端的引号并反转义
        private static string UnquotePo(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Localization 主类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 引擎全局本地化管理器。管理所有 Locale 的字符串表，
    /// 提供文本查找、富文本格式化、字体回退与运行时语言切换。
    /// 通过 <see cref="Instance"/> 单例访问。
    /// </summary>
    public sealed class Localization
    {
        // ── 单例 ──
        private static readonly Localization _instance = new();
        /// <summary>全局本地化实例</summary>
        public static Localization Instance => _instance;

        // ── Locale 与字符串表 ──
        private readonly Dictionary<string, StringTable> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LocaleInfo> _registeredLocales = new(StringComparer.OrdinalIgnoreCase);
        private LocaleInfo _currentLocale;
        private LocaleInfo? _fallbackLocale;

        // ── 字体回退 ──
        private readonly FontFallbackChain _fontFallback = new();

        // ── 缺失字符串跟踪 ──
        private readonly HashSet<string> _missingKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MissingStringRecord> _missingRecords = new();
        private const int MaxMissingRecords = 1000;

        // ── 配置 ──
        /// <summary>缺失键的默认返回文本（如 "[MISSING: {key}]"）</summary>
        public string MissingTextFormat { get; set; } = "[MISSING:{0}]";
        /// <summary>是否在查找缺失键时记录并输出警告</summary>
        public bool LogMissingStrings { get; set; } = true;
        /// <summary>是否启用热重载（监视文件变更）</summary>
        public bool HotReloadEnabled { get; set; } = true;
        /// <summary>热重载监视的文件路径集合</summary>
        private readonly Dictionary<string, FileSystemWatcher?> _watchers = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>文件路径 -> (Locale, 格式) 映射，用于热重载</summary>
        private readonly Dictionary<string, (LocaleInfo locale, string format)> _reloadMap = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前 Locale（语言切换会触发 <see cref="OnLocaleChanged"/>）</summary>
        public LocaleInfo CurrentLocale => _currentLocale;

        /// <summary>回退 Locale（当当前语言缺失某键时尝试该语言）</summary>
        public LocaleInfo? FallbackLocale
        {
            get => _fallbackLocale;
            set => _fallbackLocale = value;
        }

        /// <summary>字体回退链</summary>
        public FontFallbackChain FontFallback => _fontFallback;

        /// <summary>已注册的所有 Locale</summary>
        public IReadOnlyCollection<LocaleInfo> RegisteredLocales => _registeredLocales.Values;

        /// <summary>当前语言下已加载的字符串表条目数</summary>
        public int CurrentTableCount => _tables.TryGetValue(_currentLocale.Tag, out var t) ? t.Count : 0;

        /// <summary>语言切换事件。参数：旧 Locale、新 Locale</summary>
        public event Action<LocaleInfo, LocaleInfo>? OnLocaleChanged;

        /// <summary>热重载完成事件。参数：重载的 Locale</summary>
        public event Action<LocaleInfo>? OnTableReloaded;

        /// <summary>缺失字符串事件。参数：缺失记录</summary>
        public event Action<MissingStringRecord>? OnMissingString;

        private Localization()
        {
            // 注册内置 Locale
            foreach (var locale in LocalePresets.All)
                _registeredLocales[locale.Tag] = locale;

            _currentLocale = LocalePresets.EnglishUS;
            _fallbackLocale = LocalePresets.EnglishUS;

            // 初始化默认字体回退链
            InitializeDefaultFontFallbacks();
        }

        private void InitializeDefaultFontFallbacks()
        {
            // 拉丁语系：Roboto 优先，回退到 Arial
            foreach (var tag in new[] { "en", "fr", "de", "es", "pt", "ru" })
                _fontFallback.Set(tag, "Roboto", "Arial", "NotoSans");
            // 中文：思源黑体优先
            _fontFallback.Set("zh", "NotoSansSC", "NotoSansTC", "Microsoft YaHei", "SimSun");
            _fontFallback.Set("ja", "NotoSansJP", "Yu Gothic", "MS Gothic");
            _fontFallback.Set("ko", "NotoSansKR", "Malgun Gothic", "Dotum");
            _fontFallback.Set("ar", "NotoSansArabic", "Segoe UI", "Traditional Arabic");
        }

        // ── Locale 管理 ──

        /// <summary>注册一个自定义 Locale</summary>
        public void RegisterLocale(LocaleInfo locale)
        {
            if (locale == null) throw new ArgumentNullException(nameof(locale));
            _registeredLocales[locale.Tag] = locale;
        }

        /// <summary>判断某 Locale 是否已注册</summary>
        public bool IsLocaleRegistered(string tag) => _registeredLocales.ContainsKey(tag);

        /// <summary>获取已注册的 Locale 信息</summary>
        public LocaleInfo? GetLocale(string tag) =>
            _registeredLocales.TryGetValue(tag, out var l) ? l : null;

        /// <summary>
        /// 切换当前语言。会触发 <see cref="OnLocaleChanged"/> 事件，
        /// UI 系统可订阅该事件刷新所有显示文本。
        /// </summary>
        public void SetLocale(string tag)
        {
            if (!_registeredLocales.TryGetValue(tag, out var locale))
                throw new ArgumentException($"未注册的 Locale: {tag}", nameof(tag));

            if (ReferenceEquals(locale, _currentLocale)) return;

            var old = _currentLocale;
            _currentLocale = locale;
            OnLocaleChanged?.Invoke(old, locale);
        }

        /// <summary>切换当前语言（直接传入 LocaleInfo）</summary>
        public void SetLocale(LocaleInfo locale)
        {
            if (locale == null) throw new ArgumentNullException(nameof(locale));
            if (!_registeredLocales.ContainsKey(locale.Tag))
                _registeredLocales[locale.Tag] = locale;

            if (ReferenceEquals(locale, _currentLocale)) return;
            var old = _currentLocale;
            _currentLocale = locale;
            OnLocaleChanged?.Invoke(old, locale);
        }

        // ── 字符串表管理 ──

        /// <summary>注册一个字符串表（替换该 Locale 已有的表）</summary>
        public void RegisterTable(StringTable table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            _tables[table.LocaleTag] = table;
        }

        /// <summary>获取指定 Locale 的字符串表（不存在返回 null）</summary>
        public StringTable? GetTable(string localeTag) =>
            _tables.TryGetValue(localeTag, out var t) ? t : null;

        /// <summary>获取当前语言的字符串表</summary>
        public StringTable? GetCurrentTable() => GetTable(_currentLocale.Tag);

        /// <summary>
        /// 从文件加载字符串表并注册，同时建立热重载监视。
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <param name="locale">目标 Locale</param>
        /// <param name="format">"json" / "csv" / "po"</param>
        public void LoadTable(string path, LocaleInfo locale, string format)
        {
            var table = LoadTableFromFile(path, locale, format);
            RegisterTable(table);

            if (HotReloadEnabled)
                SetupHotReload(path, locale, format);
        }

        private StringTable LoadTableFromFile(string path, LocaleInfo locale, string format)
        {
            return format.ToLowerInvariant() switch
            {
                "json" => StringTableLoader.FromJsonFile(path, locale),
                "csv" => StringTableLoader.FromCsvFile(path, locale),
                "po" => StringTableLoader.FromPoFile(path, locale),
                _ => throw new ArgumentException($"不支持的格式: {format}")
            };
        }

        /// <summary>为指定文件设置热重载监视</summary>
        private void SetupHotReload(string path, LocaleInfo locale, string format)
        {
            var dir = Path.GetDirectoryName(path);
            var file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _reloadMap[path] = (locale, format);

            if (_watchers.ContainsKey(path)) return;

            try
            {
                var watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += (s, e) => OnFileChanged(path);
                _watchers[path] = watcher;
            }
            catch
            {
                // 某些平台/权限下无法创建监视器，静默降级
                _watchers[path] = null;
            }
        }

        private void OnFileChanged(string path)
        {
            if (!_reloadMap.TryGetValue(path, out var info)) return;
            // 文件写入可能未完成，稍作延迟后重试读取
            System.Threading.Thread.Sleep(50);
            try
            {
                var table = LoadTableFromFile(path, info.locale, info.format);
                RegisterTable(table);
                OnTableReloaded?.Invoke(info.locale);
            }
            catch
            {
                // 重载失败则忽略，保持旧表
            }
        }

        /// <summary>手动重载某 Locale 的表（从已记录的路径）</summary>
        public void ReloadTable(LocaleInfo locale)
        {
            foreach (var kvp in _reloadMap)
            {
                if (kvp.Value.locale.Tag.Equals(locale.Tag, StringComparison.OrdinalIgnoreCase))
                    OnFileChanged(kvp.Key);
            }
        }

        /// <summary>停止所有热重载监视</summary>
        public void StopAllWatchers()
        {
            foreach (var w in _watchers.Values)
            {
                if (w != null)
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
            }
            _watchers.Clear();
        }

        // ── 文本查找 ──

        /// <summary>
        /// 查找本地化文本。依次在当前语言表、回退语言表中查找。
        /// 若均未找到，返回 <see cref="MissingTextFormat"/> 格式化的默认值并记录警告。
        /// </summary>
        /// <param name="key">完整键名，如 "UI.MainMenu.StartGame"</param>
        public string Get(string key)
        {
            return Get(key, FormatArguments.Empty);
        }

        /// <summary>带格式化参数查找本地化文本</summary>
        public string Get(string key, FormatArguments args)
        {
            if (TryGetEntry(key, out var entry, out var foundLocale))
            {
                var text = SelectVariant(entry, args, foundLocale);
                return Format(text, args);
            }

            // 记录缺失
            RecordMissing(key);
            return string.Format(MissingTextFormat, key);
        }

        /// <summary>带格式化参数查找（params 便捷形式：键, arg1, val1, arg2, val2...）</summary>
        public string Get(string key, params object[] nameValuePairs)
        {
            var args = new FormatArguments();
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                args.With(nameValuePairs[i]?.ToString() ?? "", nameValuePairs[i + 1]);
            return Get(key, args);
        }

        /// <summary>尝试获取本地化文本（不记录缺失，不格式化）</summary>
        public bool TryGet(string key, out string text)
        {
            if (TryGetEntry(key, out var entry, out _))
            {
                text = entry.Text;
                return true;
            }
            text = "";
            return false;
        }

        /// <summary>内部：在当前表与回退表中查找条目</summary>
        private bool TryGetEntry(string key, out LocalizedStringEntry entry, out LocaleInfo foundLocale)
        {
            entry = null!;
            foundLocale = _currentLocale;

            // 1. 当前语言表
            if (_tables.TryGetValue(_currentLocale.Tag, out var table) && table.TryGet(key, out entry!))
                return true;

            // 2. 回退语言表
            if (_fallbackLocale != null &&
                _tables.TryGetValue(_fallbackLocale.Tag, out var fbTable) &&
                fbTable.TryGet(key, out entry!))
            {
                foundLocale = _fallbackLocale;
                return true;
            }

            return false;
        }

        /// <summary>根据参数选择复数 / 性别变体</summary>
        private string SelectVariant(LocalizedStringEntry entry, FormatArguments args, LocaleInfo locale)
        {
            // 性别变体优先
            if (args.Has("gender"))
            {
                var g = args.Get("gender")?.ToString()?.ToLowerInvariant();
                string? genderText = g switch
                {
                    "m" or "male" => entry.MasculineText,
                    "f" or "female" => entry.FeminineText,
                    "n" or "neutral" => entry.NeutralText,
                    _ => null
                };
                if (!string.IsNullOrEmpty(genderText)) return genderText;
            }

            // 复数形式
            if (entry.Plurals != null && entry.Plurals.Length > 0 && args.TryGetInt("count", out int count))
            {
                int idx = GetPluralIndex(locale.LanguageCode, count);
                if (idx >= 0 && idx < entry.Plurals.Length && !string.IsNullOrEmpty(entry.Plurals[idx]))
                    return entry.Plurals[idx];
            }

            return entry.Text;
        }

        /// <summary>
        /// 根据语言码与数量返回复数索引。
        /// 参考 CLDR 复数规则，简化实现覆盖常见语言。
        /// </summary>
        private int GetPluralIndex(string languageCode, int count)
        {
            // 简化版 CLDR 规则：
            //  中文/日文/韩文：只有一种形式（索引 0）
            //  英文/德文/西语/葡语：one(=1) / other
            //  法文：one(=0,1) / other
            //  俄文：one(以1结尾非11) / few(2-4非12-14) / many(其它) / other
            //  阿拉伯文：zero/one/few/many/other（6 种）
            return languageCode.ToLowerInvariant() switch
            {
                "zh" or "ja" or "ko" or "th" or "vi" => 0,
                "en" or "de" or "es" or "pt" or "it" or "nl" => count == 1 ? 1 : 2,
                "fr" => (count == 0 || count == 1) ? 1 : 2,
                "ru" or "uk" or "pl" => count switch
                {
                    1 => 1,
                    int c when c % 10 >= 2 && c % 10 <= 4 && (c % 100 < 10 || c % 100 >= 20) => 2,
                    _ => 3
                },
                "ar" => count switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 2,
                    int c when c % 100 >= 3 && c % 100 <= 10 => 3,
                    int c when c % 100 >= 11 => 4,
                    _ => 4
                },
                _ => count == 1 ? 1 : 2
            };
        }

        // ── 富文本格式化 ──

        // 匹配 {name} 形式的变量插值
        private static readonly Regex InterpolationPattern =
            new(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.Compiled);

        /// <summary>
        /// 富文本格式化。支持 {name} 变量插值。
        /// 例如："欢迎, {player}!" + args(player="张三") => "欢迎, 张三!"
        /// </summary>
        public string Format(string text, FormatArguments args)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text;

            return InterpolationPattern.Replace(text, m =>
            {
                string name = m.Groups[1].Value;
                if (args.Has(name))
                {
                    var val = args.Get(name);
                    return val?.ToString() ?? "";
                }
                // 未找到参数则保留原样
                return m.Value;
            });
        }

        /// <summary>便捷格式化：文本 + params 键值对</summary>
        public string Format(string text, params object[] nameValuePairs)
        {
            var args = new FormatArguments();
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                args.With(nameValuePairs[i]?.ToString() ?? "", nameValuePairs[i + 1]);
            return Format(text, args);
        }

        // ── 缺失字符串跟踪 ──

        /// <summary>记录缺失的键（去重）</summary>
        private void RecordMissing(string key)
        {
            if (!LogMissingStrings) return;

            bool isNew;
            lock (_missingKeys)
            {
                isNew = _missingKeys.Add(key);
            }

            if (isNew)
            {
                var record = new MissingStringRecord(key, _currentLocale.Tag);
                lock (_missingRecords)
                {
                    if (_missingRecords.Count < MaxMissingRecords)
                        _missingRecords.Add(record);
                }
                OnMissingString?.Invoke(record);
            }
        }

        /// <summary>获取所有缺失键（开发期用于定位未翻译条目）</summary>
        public IReadOnlyList<MissingStringRecord> GetMissingRecords()
        {
            lock (_missingRecords) return new List<MissingStringRecord>(_missingRecords);
        }

        /// <summary>清空缺失记录</summary>
        public void ClearMissingRecords()
        {
            lock (_missingKeys) _missingKeys.Clear();
            lock (_missingRecords) _missingRecords.Clear();
        }

        /// <summary>导出缺失键列表为文本（每行一个键）</summary>
        public string ExportMissingKeys()
        {
            lock (_missingKeys) return string.Join("\n", _missingKeys);
        }

        // ── 字体回退 ──

        /// <summary>获取当前语言的字体回退链</summary>
        public IReadOnlyList<string> GetCurrentFontFallback() =>
            _fontFallback.Get(_currentLocale.LanguageCode);

        /// <summary>获取当前语言的首选字体</summary>
        public string? GetCurrentPrimaryFont() =>
            _fontFallback.GetPrimary(_currentLocale.LanguageCode);

        // ── 调试 / 统计 ──

        /// <summary>获取本地化系统统计信息</summary>
        public LocalizationStats GetStats()
        {
            var stats = new LocalizationStats
            {
                CurrentLocale = _currentLocale.Tag,
                FallbackLocale = _fallbackLocale?.Tag ?? "",
                RegisteredLocaleCount = _registeredLocales.Count,
                LoadedTableCount = _tables.Count,
                MissingKeyCount = _missingKeys.Count
            };

            if (_tables.TryGetValue(_currentLocale.Tag, out var t))
                stats.CurrentTableEntryCount = t.Count;

            return stats;
        }

        /// <summary>重置本地化系统（清空所有表与记录，恢复默认语言）</summary>
        public void Reset()
        {
            StopAllWatchers();
            _reloadMap.Clear();
            _tables.Clear();
            lock (_missingKeys) _missingKeys.Clear();
            lock (_missingRecords) _missingRecords.Clear();
            _currentLocale = LocalePresets.EnglishUS;
            _fallbackLocale = LocalePresets.EnglishUS;
        }
    }

    /// <summary>本地化系统统计信息</summary>
    public sealed class LocalizationStats
    {
        /// <summary>当前语言标签</summary>
        public string CurrentLocale { get; set; } = "";
        /// <summary>回退语言标签</summary>
        public string FallbackLocale { get; set; } = "";
        /// <summary>已注册语言数</summary>
        public int RegisteredLocaleCount { get; set; }
        /// <summary>已加载字符串表数</summary>
        public int LoadedTableCount { get; set; }
        /// <summary>当前语言表条目数</summary>
        public int CurrentTableEntryCount { get; set; }
        /// <summary>缺失键数量</summary>
        public int MissingKeyCount { get; set; }

        public override string ToString() =>
            $"Locale: {CurrentLocale} (fallback: {FallbackLocale}), " +
            $"Tables: {LoadedTableCount}, Entries: {CurrentTableEntryCount}, Missing: {MissingKeyCount}";
    }

    // ══════════════════════════════════════════════════════════════════
    // 引擎集成入口
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化系统管理器。封装初始化与引擎主循环的对接，
    /// 负责在启动时加载默认语言表，并在退出时释放文件监视器。
    /// </summary>
    public sealed class LocalizationSystem
    {
        private readonly Localization _loc = Localization.Instance;

        /// <summary>当前语言</summary>
        public LocaleInfo CurrentLocale => _loc.CurrentLocale;

        /// <summary>初始化本地化系统，设置默认语言与回退语言</summary>
        public void Initialize(string defaultLocaleTag = "en-US", string fallbackLocaleTag = "en-US")
        {
            if (_loc.IsLocaleRegistered(defaultLocaleTag))
                _loc.SetLocale(defaultLocaleTag);

            var fb = _loc.GetLocale(fallbackLocaleTag);
            if (fb != null) _loc.FallbackLocale = fb;
        }

        /// <summary>切换语言</summary>
        public void SetLocale(string tag) => _loc.SetLocale(tag);

        /// <summary>查找文本</summary>
        public string Get(string key) => _loc.Get(key);

        /// <summary>查找文本（带参数）</summary>
        public string Get(string key, FormatArguments args) => _loc.Get(key, args);

        /// <summary>从文件加载字符串表</summary>
        public void LoadTable(string path, LocaleInfo locale, string format) =>
            _loc.LoadTable(path, locale, format);

        /// <summary>获取 HUD 统计文本</summary>
        public string GetStatsText() => _loc.GetStats().ToString();

        /// <summary>关闭系统（停止文件监视）</summary>
        public void Shutdown() => _loc.StopAllWatchers();
    }
}
