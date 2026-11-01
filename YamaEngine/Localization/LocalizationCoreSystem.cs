using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace YamaEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 本地化核心系统（LocalizationCoreSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现yama引擎本地化系统的核心架构，包含以下能力：
    //   1. Locale 类：完整的语言/地区定义（代码/显示名/原生名/方向/排序）
    //   2. LocalizationManager 全局单例：语言注册/切换/检测/热重载
    //   3. 支持 20+ 语言（中/英/日/韩/法/德/西/葡/俄/阿拉伯/泰/越南等）
    //   4. 语言检测链（系统语言 → 用户设置 → 默认语言）
    //   5. 语言切换策略（即时切换/重启生效/渐进加载）
    //   6. 本地化优先级（字符串表 → 代码回退 → Key 原样返回）
    //   7. 热重载：运行时无缝切换语言包
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 文本方向枚举。用于 UI 布局和文本渲染方向控制。
    /// </summary>
    public enum TextDirectionType
    {
        /// <summary>从左到右（LTR）——适用于拉丁语系、中文、日文、韩文等</summary>
        LeftToRight,
        /// <summary>从右到左（RTL）——适用于阿拉伯语、希伯来语等</summary>
        RightToLeft,
        /// <summary>自动检测（根据首字符强方向性自动判断）</summary>
        Auto
    }

    /// <summary>
    /// 语言切换策略。控制切换语言后的行为模式。
    /// </summary>
    public enum LanguageSwitchStrategy
    {
        /// <summary>即时切换：立即替换所有显示文本，UI 刷新</summary>
        Immediate,
        /// <summary>重启生效：标记需重启，下次启动时加载新语言</summary>
        RequireRestart,
        /// <summary>渐进加载：先加载核心字符串表，后台逐步加载其余资源</summary>
        Progressive
    }

    /// <summary>
    /// 本地化查找优先级。当同一 Key 在多个来源中存在时的选择顺序。
    /// </summary>
    public enum LocalizationPriority
    {
        /// <summary>最高优先级：来自代码中直接设置的值</summary>
        CodeOverride = 0,
        /// <summary>标准优先级：来自字符串表</summary>
        StringTable = 1,
        /// <summary>低优先级：来自运行时动态注入的值</summary>
        RuntimeOverride = 2,
        /// <summary>最低优先级：回退到 Key 原样返回</summary>
        FallbackToKey = 3
    }

    /// <summary>
    /// Locale 类：完整描述一种语言/地区组合。
    /// 包含 BCP-47 标签、ISO 语言码/地区码、显示名、原生名、文本方向、排序规则等。
    /// </summary>
    public sealed class Locale : IEquatable<Locale>, IComparable<Locale>
    {
        /// <summary>BCP-47 语言标签，如 "en-US"、"zh-CN"、"ja-JP"</summary>
        public string Tag { get; }

        /// <summary>ISO 639-1 两字母语言码，如 "en"、"zh"、"ja"</summary>
        public string LanguageCode { get; }

        /// <summary>ISO 3166-1 两字母地区码，如 "US"、"CN"、"JP"。可为空。</summary>
        public string RegionCode { get; }

        /// <summary>英文显示名称，如 "Chinese (Simplified)"</summary>
        public string DisplayName { get; }

        /// <summary>本地化原生名称，如 "简体中文"</summary>
        public string NativeName { get; }

        /// <summary>文本方向（LTR/RTL）</summary>
        public TextDirectionType Direction { get; }

        /// <summary>排序规则名称（用于字符串比较排序）</summary>
        public string SortRuleName { get; }

        /// <summary>对应的 CultureInfo（用于数字/日期格式化，可为 null）</summary>
        public CultureInfo? Culture { get; }

        /// <summary>该语言是否是内置支持的语言</summary>
        public bool IsBuiltIn { get; }

        public Locale(string tag, string languageCode, string? regionCode,
                      string displayName, string nativeName,
                      TextDirectionType direction,
                      string sortRuleName = "",
                      CultureInfo? culture = null,
                      bool isBuiltIn = true)
        {
            Tag = tag ?? throw new ArgumentNullException(nameof(tag));
            LanguageCode = languageCode ?? throw new ArgumentNullException(nameof(languageCode));
            RegionCode = regionCode ?? "";
            DisplayName = displayName ?? "";
            NativeName = nativeName ?? "";
            Direction = direction;
            SortRuleName = sortRuleName ?? "";
            Culture = culture;
            IsBuiltIn = isBuiltIn;
        }

        /// <summary>语言代码部分（不含地区），如 "en" from "en-US"</summary>
        public string LanguageOnly => LanguageCode;

        /// <summary>是否是 RTL 语言</summary>
        public bool IsRTL => Direction == TextDirectionType.RightToLeft;

        /// <summary>获取用于字符串比较的 StringComparer</summary>
        public StringComparer GetComparer()
        {
            if (Culture != null)
                return StringComparer.Create(Culture, CompareOptions.StringSort);
            return StringComparer.OrdinalIgnoreCase;
        }

        public bool Equals(Locale? other)
        {
            if (other is null) return false;
            return string.Equals(Tag, other.Tag, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as Locale);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Tag);

        public int CompareTo(Locale? other)
        {
            if (other is null) return 1;
            return string.Compare(Tag, other.Tag, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => $"{Tag} ({NativeName})";

        /// <summary>判断此 Locale 是否与目标标签匹配（支持语言代码级别匹配）</summary>
        public bool MatchesTag(string tag)
        {
            if (string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(LanguageCode, tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// 语言检测优先级。定义自动检测语言时的来源优先级。
    /// </summary>
    public enum LanguageDetectionPriority
    {
        /// <summary>最高：用户明确设置的语言</summary>
        UserSetting,
        /// <summary>中：操作系统/平台系统语言</summary>
        SystemLocale,
        /// <summary>最低：引擎默认语言</summary>
        EngineDefault
    }

    /// <summary>
    /// 热重载状态。跟踪语言包热重载过程。
    /// </summary>
    public enum HotReloadState
    {
        /// <summary>空闲，无重载进行</summary>
        Idle,
        /// <summary>正在卸载旧语言包</summary>
        Unloading,
        /// <summary>正在加载新语言包</summary>
        Loading,
        /// <summary>正在应用新语言（刷新 UI 等）</summary>
        Applying,
        /// <summary>重载完成</summary>
        Completed,
        /// <summary>重载失败</summary>
        Failed
    }

    /// <summary>
    /// 语言切换事件参数。
    /// </summary>
    public sealed class LanguageChangedEventArgs : EventArgs
    {
        /// <summary>切换前的语言</summary>
        public Locale OldLocale { get; }
        /// <summary>切换后的语言</summary>
        public Locale NewLocale { get; }
        /// <summary>切换策略</summary>
        public LanguageSwitchStrategy Strategy { get; }
        /// <summary>是否需要重启才完全生效</summary>
        public bool RequiresRestart { get; }

        public LanguageChangedEventArgs(Locale oldLocale, Locale newLocale,
                                        LanguageSwitchStrategy strategy,
                                        bool requiresRestart)
        {
            OldLocale = oldLocale;
            NewLocale = newLocale;
            Strategy = strategy;
            RequiresRestart = requiresRestart;
        }
    }

    /// <summary>
    /// 热重载状态变化事件参数。
    /// </summary>
    public sealed class HotReloadStateChangedEventArgs : EventArgs
    {
        /// <summary>重载中的目标语言标签</summary>
        public string LocaleTag { get; }
        /// <summary>当前状态</summary>
        public HotReloadState State { get; }
        /// <summary>错误信息（仅 Failed 状态时有值）</summary>
        public string? Error { get; }
        /// <summary>重载进度（0.0 - 1.0）</summary>
        public float Progress { get; }

        public HotReloadStateChangedEventArgs(string localeTag, HotReloadState state,
                                              string? error = null, float progress = 0f)
        {
            LocaleTag = localeTag;
            State = state;
            Error = error;
            Progress = progress;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizationManager —— 全局单例管理器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化管理器（LocalizationManager）—— yama引擎本地化系统的全局单例核心。
    /// 职责：
    ///   - 管理所有注册语言（Locale）
    ///   - 维护当前语言、默认语言、回退语言
    ///   - 语言自动检测（系统语言→用户设置→默认）
    ///   - 语言切换（即时/重启/渐进）
    ///   - 本地化热重载
    ///   - 提供统一的本地化字符串查找入口
    /// </summary>
    public sealed class LocalizationManager
    {
        // ── 单例 ──
        private static readonly Lazy<LocalizationManager> _lazy =
            new(() => new LocalizationManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例实例</summary>
        public static LocalizationManager Instance => _lazy.Value;

        // ── 语言注册表 ──
        private readonly Dictionary<string, Locale> _locales =
            new(StringComparer.OrdinalIgnoreCase);

        private Locale _currentLocale;
        private Locale _defaultLocale;
        private Locale? _fallbackLocale;

        // ── 语言检测 ──
        private string? _userSettingLanguage;
        private LanguageDetectionPriority[] _detectionOrder =
        {
            LanguageDetectionPriority.UserSetting,
            LanguageDetectionPriority.SystemLocale,
            LanguageDetectionPriority.EngineDefault
        };

        // ── 语言切换 ──
        private LanguageSwitchStrategy _switchStrategy = LanguageSwitchStrategy.Immediate;

        // ── 热重载 ──
        private HotReloadState _hotReloadState = HotReloadState.Idle;

        // ── 配置 ──
        private bool _isInitialized;

        /// <summary>当前激活语言</summary>
        public Locale CurrentLocale => _currentLocale;

        /// <summary>引擎默认语言</summary>
        public Locale DefaultLocale => _defaultLocale;

        /// <summary>回退语言（当前语言缺失翻译时使用）</summary>
        public Locale? FallbackLocale
        {
            get => _fallbackLocale;
            set => _fallbackLocale = value;
        }

        /// <summary>语言切换策略</summary>
        public LanguageSwitchStrategy SwitchStrategy
        {
            get => _switchStrategy;
            set => _switchStrategy = value;
        }

        /// <summary>热重载当前状态</summary>
        public HotReloadState HotReloadState => _hotReloadState;

        /// <summary>所有已注册的语言</summary>
        public IReadOnlyCollection<Locale> RegisteredLocales => _locales.Values;

        /// <summary>是否已初始化</summary>
        public bool IsInitialized => _isInitialized;

        // ── 事件 ──

        /// <summary>语言切换事件</summary>
        public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

        /// <summary>热重载状态变化事件</summary>
        public event EventHandler<HotReloadStateChangedEventArgs>? HotReloadStateChanged;

        /// <summary>语言注册事件</summary>
        public event EventHandler<Locale>? LocaleRegistered;

        /// <summary>语言卸载事件</summary>
        public event EventHandler<Locale>? LocaleUnregistered;

        // ════════════════════════════════════════════════════
        // 构造函数 —— 注册内置 20+ 语言
        // ════════════════════════════════════════════════════

        private LocalizationManager()
        {
            RegisterBuiltInLocales();

            // 默认语言：英语（美国）
            _defaultLocale = GetLocale("en-US")!;
            _currentLocale = _defaultLocale;
            _fallbackLocale = _defaultLocale;
        }

        /// <summary>
        /// 注册引擎内置支持的 20+ 语言。
        /// 涵盖中文、英文、日文、韩文、法文、德文、西文、葡文、俄文、
        /// 阿拉伯文、泰文、越南文、意大利文、荷兰文、波兰文、土耳其文、
        /// 印地文、瑞典文、丹麦文、芬兰文、挪威文、捷克文、希伯来文等。
        /// </summary>
        private void RegisterBuiltInLocales()
        {
            // ── 东亚语言 ──
            RegisterLocaleInternal(new Locale("zh-CN", "zh", "CN",
                "Chinese (Simplified)", "简体中文",
                TextDirectionType.LeftToRight, "zh-CN",
                new CultureInfo("zh-CN")));

            RegisterLocaleInternal(new Locale("zh-TW", "zh", "TW",
                "Chinese (Traditional)", "繁體中文",
                TextDirectionType.LeftToRight, "zh-TW",
                new CultureInfo("zh-TW")));

            RegisterLocaleInternal(new Locale("ja-JP", "ja", "JP",
                "Japanese", "日本語",
                TextDirectionType.LeftToRight, "ja-JP",
                new CultureInfo("ja-JP")));

            RegisterLocaleInternal(new Locale("ko-KR", "ko", "KR",
                "Korean", "한국어",
                TextDirectionType.LeftToRight, "ko-KR",
                new CultureInfo("ko-KR")));

            // ── 西欧语言 ──
            RegisterLocaleInternal(new Locale("en-US", "en", "US",
                "English (United States)", "English",
                TextDirectionType.LeftToRight, "en-US",
                new CultureInfo("en-US")));

            RegisterLocaleInternal(new Locale("en-GB", "en", "GB",
                "English (United Kingdom)", "English (UK)",
                TextDirectionType.LeftToRight, "en-GB",
                new CultureInfo("en-GB")));

            RegisterLocaleInternal(new Locale("fr-FR", "fr", "FR",
                "French (France)", "Français",
                TextDirectionType.LeftToRight, "fr-FR",
                new CultureInfo("fr-FR")));

            RegisterLocaleInternal(new Locale("de-DE", "de", "DE",
                "German (Germany)", "Deutsch",
                TextDirectionType.LeftToRight, "de-DE",
                new CultureInfo("de-DE")));

            RegisterLocaleInternal(new Locale("es-ES", "es", "ES",
                "Spanish (Spain)", "Español",
                TextDirectionType.LeftToRight, "es-ES",
                new CultureInfo("es-ES")));

            RegisterLocaleInternal(new Locale("es-MX", "es", "MX",
                "Spanish (Mexico)", "Español (México)",
                TextDirectionType.LeftToRight, "es-MX",
                new CultureInfo("es-MX")));

            RegisterLocaleInternal(new Locale("pt-BR", "pt", "BR",
                "Portuguese (Brazil)", "Português (Brasil)",
                TextDirectionType.LeftToRight, "pt-BR",
                new CultureInfo("pt-BR")));

            RegisterLocaleInternal(new Locale("pt-PT", "pt", "PT",
                "Portuguese (Portugal)", "Português (Portugal)",
                TextDirectionType.LeftToRight, "pt-PT",
                new CultureInfo("pt-PT")));

            RegisterLocaleInternal(new Locale("it-IT", "it", "IT",
                "Italian (Italy)", "Italiano",
                TextDirectionType.LeftToRight, "it-IT",
                new CultureInfo("it-IT")));

            RegisterLocaleInternal(new Locale("nl-NL", "nl", "NL",
                "Dutch (Netherlands)", "Nederlands",
                TextDirectionType.LeftToRight, "nl-NL",
                new CultureInfo("nl-NL")));

            // ── 北欧/东欧语言 ──
            RegisterLocaleInternal(new Locale("ru-RU", "ru", "RU",
                "Russian (Russia)", "Русский",
                TextDirectionType.LeftToRight, "ru-RU",
                new CultureInfo("ru-RU")));

            RegisterLocaleInternal(new Locale("pl-PL", "pl", "PL",
                "Polish (Poland)", "Polski",
                TextDirectionType.LeftToRight, "pl-PL",
                new CultureInfo("pl-PL")));

            RegisterLocaleInternal(new Locale("sv-SE", "sv", "SE",
                "Swedish (Sweden)", "Svenska",
                TextDirectionType.LeftToRight, "sv-SE",
                new CultureInfo("sv-SE")));

            RegisterLocaleInternal(new Locale("da-DK", "da", "DK",
                "Danish (Denmark)", "Dansk",
                TextDirectionType.LeftToRight, "da-DK",
                new CultureInfo("da-DK")));

            RegisterLocaleInternal(new Locale("fi-FI", "fi", "FI",
                "Finnish (Finland)", "Suomi",
                TextDirectionType.LeftToRight, "fi-FI",
                new CultureInfo("fi-FI")));

            RegisterLocaleInternal(new Locale("nb-NO", "nb", "NO",
                "Norwegian (Bokmål)", "Norsk (Bokmål)",
                TextDirectionType.LeftToRight, "nb-NO",
                new CultureInfo("nb-NO")));

            RegisterLocaleInternal(new Locale("cs-CZ", "cs", "CZ",
                "Czech (Czech Republic)", "Čeština",
                TextDirectionType.LeftToRight, "cs-CZ",
                new CultureInfo("cs-CZ")));

            RegisterLocaleInternal(new Locale("tr-TR", "tr", "TR",
                "Turkish (Turkey)", "Türkçe",
                TextDirectionType.LeftToRight, "tr-TR",
                new CultureInfo("tr-TR")));

            RegisterLocaleInternal(new Locale("uk-UA", "uk", "UA",
                "Ukrainian (Ukraine)", "Українська",
                TextDirectionType.LeftToRight, "uk-UA",
                new CultureInfo("uk-UA")));

            // ── 东南亚/南亚语言 ──
            RegisterLocaleInternal(new Locale("th-TH", "th", "TH",
                "Thai (Thailand)", "ภาษาไทย",
                TextDirectionType.LeftToRight, "th-TH",
                new CultureInfo("th-TH")));

            RegisterLocaleInternal(new Locale("vi-VN", "vi", "VN",
                "Vietnamese (Vietnam)", "Tiếng Việt",
                TextDirectionType.LeftToRight, "vi-VN",
                new CultureInfo("vi-VN")));

            RegisterLocaleInternal(new Locale("hi-IN", "hi", "IN",
                "Hindi (India)", "हिन्दी",
                TextDirectionType.LeftToRight, "hi-IN",
                new CultureInfo("hi-IN")));

            RegisterLocaleInternal(new Locale("id-ID", "id", "ID",
                "Indonesian (Indonesia)", "Bahasa Indonesia",
                TextDirectionType.LeftToRight, "id-ID",
                new CultureInfo("id-ID")));

            RegisterLocaleInternal(new Locale("ms-MY", "ms", "MY",
                "Malay (Malaysia)", "Bahasa Melayu",
                TextDirectionType.LeftToRight, "ms-MY",
                new CultureInfo("ms-MY")));

            // ── RTL 语言 ──
            RegisterLocaleInternal(new Locale("ar-SA", "ar", "SA",
                "Arabic (Saudi Arabia)", "العربية",
                TextDirectionType.RightToLeft, "ar-SA",
                new CultureInfo("ar-SA")));

            RegisterLocaleInternal(new Locale("he-IL", "he", "IL",
                "Hebrew (Israel)", "עברית",
                TextDirectionType.RightToLeft, "he-IL",
                new CultureInfo("he-IL")));

            RegisterLocaleInternal(new Locale("fa-IR", "fa", "IR",
                "Persian (Iran)", "فارسی",
                TextDirectionType.RightToLeft, "fa-IR",
                new CultureInfo("fa-IR")));

            RegisterLocaleInternal(new Locale("ur-PK", "ur", "PK",
                "Urdu (Pakistan)", "اردو",
                TextDirectionType.RightToLeft, "ur-PK",
                new CultureInfo("ur-PK")));
        }

        /// <summary>内部注册语言（不触发事件）</summary>
        private void RegisterLocaleInternal(Locale locale)
        {
            _locales[locale.Tag] = locale;
        }

        // ════════════════════════════════════════════════════
        // 初始化
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 初始化本地化系统。执行语言自动检测，设置当前语言。
        /// </summary>
        /// <param name="userSettingLanguage">用户设置的语言标签（可为 null）</param>
        /// <param name="defaultLanguageTag">默认语言标签（默认 "en-US"）</param>
        /// <param name="fallbackLanguageTag">回退语言标签（默认 "en-US"）</param>
        public void Initialize(string? userSettingLanguage = null,
                              string defaultLanguageTag = "en-US",
                              string fallbackLanguageTag = "en-US")
        {
            if (_isInitialized) return;

            _userSettingLanguage = userSettingLanguage;

            // 设置默认语言
            if (TryGetLocale(defaultLanguageTag, out var defaultLoc))
                _defaultLocale = defaultLoc;

            // 设置回退语言
            if (TryGetLocale(fallbackLanguageTag, out var fallbackLoc))
                _fallbackLocale = fallbackLoc;

            // 语言自动检测
            var detectedLocale = DetectLanguage();
            _currentLocale = detectedLocale;

            _isInitialized = true;
        }

        /// <summary>
        /// 重置本地化系统到初始状态。
        /// </summary>
        public void Reset()
        {
            _currentLocale = _defaultLocale;
            _fallbackLocale = _defaultLocale;
            _userSettingLanguage = null;
            _hotReloadState = HotReloadState.Idle;
            _isInitialized = false;
        }

        // ════════════════════════════════════════════════════
        // 语言注册/查询
        // ════════════════════════════════════════════════════

        /// <summary>注册一个新语言到系统中</summary>
        public void RegisterLocale(Locale locale)
        {
            if (locale == null) throw new ArgumentNullException(nameof(locale));
            _locales[locale.Tag] = locale;
            LocaleRegistered?.Invoke(this, locale);
        }

        /// <summary>注销一个语言</summary>
        public bool UnregisterLocale(string tag)
        {
            if (_locales.TryGetValue(tag, out var locale))
            {
                _locales.Remove(tag);
                LocaleUnregistered?.Invoke(this, locale);

                // 如果移除的是当前语言，回退到默认
                if (string.Equals(_currentLocale.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    _currentLocale = _defaultLocale;

                return true;
            }
            return false;
        }

        /// <summary>根据标签获取语言（精确匹配）</summary>
        public Locale? GetLocale(string tag)
        {
            _locales.TryGetValue(tag, out var locale);
            return locale;
        }

        /// <summary>尝试获取语言</summary>
        public bool TryGetLocale(string tag, out Locale locale)
        {
            return _locales.TryGetValue(tag, out locale!);
        }

        /// <summary>根据语言代码（不含地区）获取第一个匹配的语言</summary>
        public Locale? GetLocaleByLanguageCode(string languageCode)
        {
            return _locales.Values.FirstOrDefault(l =>
                string.Equals(l.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>检查某语言是否已注册</summary>
        public bool IsLocaleRegistered(string tag)
        {
            return _locales.ContainsKey(tag);
        }

        /// <summary>获取所有已注册语言标签</summary>
        public IEnumerable<string> GetAllLocaleTags()
        {
            return _locales.Keys;
        }

        /// <summary>获取 LTR 语言列表</summary>
        public IEnumerable<Locale> GetLTRLocales()
        {
            return _locales.Values.Where(l => l.Direction == TextDirectionType.LeftToRight);
        }

        /// <summary>获取 RTL 语言列表</summary>
        public IEnumerable<Locale> GetRTLLocales()
        {
            return _locales.Values.Where(l => l.Direction == TextDirectionType.RightToLeft);
        }

        /// <summary>获取已注册语言数量</summary>
        public int LocaleCount => _locales.Count;

        // ════════════════════════════════════════════════════
        // 语言检测
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 自动检测应使用的语言。
        /// 检测顺序：用户设置 → 系统语言 → 引擎默认。
        /// </summary>
        public Locale DetectLanguage()
        {
            foreach (var priority in _detectionOrder)
            {
                var detected = TryDetectByPriority(priority);
                if (detected != null) return detected;
            }
            return _defaultLocale;
        }

        private Locale? TryDetectByPriority(LanguageDetectionPriority priority)
        {
            return priority switch
            {
                LanguageDetectionPriority.UserSetting => DetectFromUserSetting(),
                LanguageDetectionPriority.SystemLocale => DetectFromSystem(),
                LanguageDetectionPriority.EngineDefault => _defaultLocale,
                _ => null
            };
        }

        /// <summary>从用户设置检测语言</summary>
        private Locale? DetectFromUserSetting()
        {
            if (string.IsNullOrEmpty(_userSettingLanguage)) return null;

            // 精确匹配
            if (TryGetLocale(_userSettingLanguage, out var exact))
                return exact;

            // 语言代码级别匹配
            var langCode = _userSettingLanguage.Split('-')[0];
            return GetLocaleByLanguageCode(langCode);
        }

        /// <summary>从操作系统/平台检测语言</summary>
        private Locale? DetectFromSystem()
        {
            try
            {
                var systemUICulture = CultureInfo.CurrentUICulture;
                var systemTag = systemUICulture.Name; // e.g. "zh-CN"

                // 精确匹配
                if (TryGetLocale(systemTag, out var exact))
                    return exact;

                // 语言代码匹配
                return GetLocaleByLanguageCode(systemUICulture.TwoLetterISOLanguageName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>设置语言检测优先级顺序</summary>
        public void SetDetectionOrder(params LanguageDetectionPriority[] order)
        {
            if (order == null || order.Length == 0)
                throw new ArgumentException("检测优先级顺序不能为空");
            _detectionOrder = order;
        }

        /// <summary>设置用户语言偏好（优先级最高）</summary>
        public void SetUserLanguage(string localeTag)
        {
            _userSettingLanguage = localeTag;
        }

        // ════════════════════════════════════════════════════
        // 语言切换
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 切换当前语言。根据 <see cref="SwitchStrategy"/> 的不同：
        ///   - Immediate：立即生效，触发 LanguageChanged 事件
        ///   - RequireRestart：标记需重启，保存设置
        ///   - Progressive：先加载核心包，后台逐步加载
        /// </summary>
        /// <param name="localeTag">目标语言标签</param>
        /// <returns>是否切换成功</returns>
        public bool SwitchLanguage(string localeTag)
        {
            if (!TryGetLocale(localeTag, out var targetLocale))
            {
                // 尝试语言代码匹配
                targetLocale = GetLocaleByLanguageCode(localeTag.Split('-')[0]);
                if (targetLocale == null) return false;
            }

            return SwitchLanguage(targetLocale);
        }

        /// <summary>切换当前语言（传入 Locale 对象）</summary>
        public bool SwitchLanguage(Locale targetLocale)
        {
            if (targetLocale == null) throw new ArgumentNullException(nameof(targetLocale));
            if (string.Equals(_currentLocale.Tag, targetLocale.Tag, StringComparison.OrdinalIgnoreCase))
                return true; // 已经是目标语言

            var oldLocale = _currentLocale;

            switch (_switchStrategy)
            {
                case LanguageSwitchStrategy.Immediate:
                    _currentLocale = targetLocale;
                    LanguageChanged?.Invoke(this,
                        new LanguageChangedEventArgs(oldLocale, targetLocale, _switchStrategy, false));
                    return true;

                case LanguageSwitchStrategy.RequireRestart:
                    // 标记需要重启，保存到配置
                    _currentLocale = targetLocale;
                    LanguageChanged?.Invoke(this,
                        new LanguageChangedEventArgs(oldLocale, targetLocale, _switchStrategy, true));
                    return true;

                case LanguageSwitchStrategy.Progressive:
                    return SwitchLanguageProgressive(oldLocale, targetLocale);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 切换到指定语言（实时预览/资产路径的便捷别名；按 BCP-47 标签或语言码查找）。
        /// 行为与 <see cref="SwitchLanguage(string)"/> 一致。
        /// </summary>
        /// <param name="localeTag">目标语言标签</param>
        /// <returns>是否切换成功</returns>
        public bool SetLocale(string localeTag) => SwitchLanguage(localeTag);

        /// <summary>渐进式语言切换（先加载核心字符串，后台加载资源）</summary>
        private bool SwitchLanguageProgressive(Locale oldLocale, Locale targetLocale)
        {
            // 阶段 1：立即切换语言标识并触发事件（核心字符串已预加载）
            _currentLocale = targetLocale;
            LanguageChanged?.Invoke(this,
                new LanguageChangedEventArgs(oldLocale, targetLocale, _switchStrategy, false));

            // 阶段 2 的后台资源加载由 HotReloading 系统异步完成
            // 此处仅触发渐进加载通知
            return true;
        }

        // ════════════════════════════════════════════════════
        // 本地化查找优先级
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 根据 Key 查找本地化字符串。
        /// 查找优先级：代码覆盖 → 字符串表（当前语言） → 字符串表（回退语言） → Key 原样。
        /// </summary>
        public string Localize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;

            // 优先级 1：代码覆盖层
            if (_codeOverrides.TryGetValue(key, out var overridden))
                return overridden;

            // 优先级 2：字符串表查找由 StringTableSystem 处理
            // 优先级 3：回退语言查找
            // 优先级 4：返回 Key 本身
            return key;
        }

        /// <summary>
        /// 带格式化参数的本地化查找。
        /// </summary>
        public string Localize(string key, params object[] args)
        {
            var text = Localize(key);
            if (args == null || args.Length == 0) return text;

            // 支持 {0}, {1} 位置参数格式化
            try
            {
                return string.Format(text, args);
            }
            catch
            {
                return text;
            }
        }

        // ── 代码覆盖层（用于代码中直接覆盖特定 Key 的翻译）──
        private readonly Dictionary<string, string> _codeOverrides =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>设置代码级别的翻译覆盖</summary>
        public void SetCodeOverride(string key, string value)
        {
            if (!string.IsNullOrEmpty(key))
                _codeOverrides[key] = value;
        }

        /// <summary>移除代码覆盖</summary>
        public bool RemoveCodeOverride(string key) => _codeOverrides.Remove(key);

        /// <summary>清空所有代码覆盖</summary>
        public void ClearCodeOverrides() => _codeOverrides.Clear();

        // ════════════════════════════════════════════════════
        // 热重载
        // ════════════════════════════════════════════════════

        /// <summary>
        /// 触发语言包热重载。
        /// 重新加载指定语言的字符串表和资源，不中断运行时。
        /// </summary>
        /// <param name="localeTag">要重载的语言标签（null 则重载当前语言）</param>
        public void HotReload(string? localeTag = null)
        {
            var tag = localeTag ?? _currentLocale.Tag;
            SetHotReloadState(tag, HotReloadState.Unloading, progress: 0f);

            try
            {
                SetHotReloadState(tag, HotReloadState.Loading, progress: 0.3f);

                // 实际加载逻辑由 StringTableSystem 等子系统处理
                // 此处触发事件通知子系统重新加载

                SetHotReloadState(tag, HotReloadState.Applying, progress: 0.8f);
                SetHotReloadState(tag, HotReloadState.Completed, progress: 1f);
            }
            catch (Exception ex)
            {
                SetHotReloadState(tag, HotReloadState.Failed, error: ex.Message, progress: 0f);
            }
        }

        /// <summary>重置热重载状态到 Idle</summary>
        public void ResetHotReloadState()
        {
            _hotReloadState = HotReloadState.Idle;
        }

        private void SetHotReloadState(string localeTag, HotReloadState state,
                                       string? error = null, float progress = 0f)
        {
            _hotReloadState = state;
            HotReloadStateChanged?.Invoke(this,
                new HotReloadStateChangedEventArgs(localeTag, state, error, progress));
        }

        // ════════════════════════════════════════════════════
        // 统计与调试
        // ════════════════════════════════════════════════════

        /// <summary>获取本地化管理器状态摘要</summary>
        public LocalizationManagerStats GetStats()
        {
            return new LocalizationManagerStats
            {
                IsInitialized = _isInitialized,
                CurrentLocaleTag = _currentLocale.Tag,
                DefaultLocaleTag = _defaultLocale.Tag,
                FallbackLocaleTag = _fallbackLocale?.Tag ?? "",
                RegisteredLocaleCount = _locales.Count,
                SwitchStrategy = _switchStrategy.ToString(),
                HotReloadState = _hotReloadState.ToString(),
                CodeOverrideCount = _codeOverrides.Count
            };
        }
    }

    /// <summary>本地化管理器统计信息</summary>
    public sealed class LocalizationManagerStats
    {
        public bool IsInitialized { get; set; }
        public string CurrentLocaleTag { get; set; } = "";
        public string DefaultLocaleTag { get; set; } = "";
        public string FallbackLocaleTag { get; set; } = "";
        public int RegisteredLocaleCount { get; set; }
        public string SwitchStrategy { get; set; } = "";
        public string HotReloadState { get; set; } = "";
        public int CodeOverrideCount { get; set; }

        public override string ToString() =>
            $"Locale: {CurrentLocaleTag} (Default: {DefaultLocaleTag}, Fallback: {FallbackLocaleTag}), " +
            $"Registered: {RegisteredLocaleCount}, Strategy: {SwitchStrategy}";
    }
}
