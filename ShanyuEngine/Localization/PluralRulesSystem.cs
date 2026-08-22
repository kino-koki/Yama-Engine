using System;
using System.Collections.Generic;
using System.Linq;

namespace ShanyuEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 复数规则系统（PluralRulesSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现基于 CLDR（Unicode Common Locale Data Repository）标准的复数规则系统。
    //   1. PluralForm 枚举：Zero/One/Two/Few/Many/Other
    //   2. PluralRule：可执行复数规则（由表达式定义）
    //   3. PluralRuleSet：语言→复数规则映射
    //   4. PluralFormatter：复数字符串格式化
    //   5. 内置 30+ 种语言的 CLDR 复数规则
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// CLDR 复数形式枚举。
    /// 不同语言有不同数量的复数类别，此枚举覆盖所有可能的类别。
    /// </summary>
    public enum PluralForm
    {
        /// <summary>零（仅阿拉伯语等使用）</summary>
        Zero,
        /// <summary>一（英文: 1 item）</summary>
        One,
        /// <summary>二（仅阿拉伯语、斯洛文尼亚语等使用）</summary>
        Two,
        /// <summary>少数（俄语: 2-4, 波兰语: 2-4）</summary>
        Few,
        /// <summary>许多（俄语: 5-20, 波兰语: 5+）</summary>
        Many,
        /// <summary>其他（默认/兜底形式）</summary>
        Other
    }

    // ══════════════════════════════════════════════════════════════════
    // PluralRule —— 复数规则
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 复数规则。根据数值判断应使用的复数形式。
    /// 每条规则关联一个 PluralForm 和一个条件判断函数。
    /// 支持从 CLDR 规则表达式解析。
    /// </summary>
    public sealed class PluralRule
    {
        /// <summary>该规则对应的复数形式</summary>
        public PluralForm Form { get; }

        /// <summary>CLDR 规则表达式（如 "i = 1 and v = 0"）</summary>
        public string RuleExpression { get; }

        /// <summary>条件判断函数</summary>
        private readonly Func<decimal, bool> _condition;

        /// <summary>是否使用 CLDR 表达式（用于序列化/调试）</summary>
        public bool UsesExpression { get; }

        /// <summary>
        /// 创建基于函数的复数规则。
        /// </summary>
        public PluralRule(PluralForm form, Func<decimal, bool> condition,
                          string ruleExpression = "")
        {
            Form = form;
            _condition = condition ?? throw new ArgumentNullException(nameof(condition));
            RuleExpression = ruleExpression;
            UsesExpression = !string.IsNullOrEmpty(ruleExpression);
        }

        /// <summary>判断给定数值是否匹配此复数形式</summary>
        public bool Matches(decimal number) => _condition(number);

        /// <summary>获取所有 CLDR 标准规则</summary>
        public override string ToString() => $"{Form}: {RuleExpression}";
    }

    // ══════════════════════════════════════════════════════════════════
    // PluralRuleSet —— 语言复数规则集
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 复数规则集。一种语言的所有复数形式规则组合。
    /// 根据数值按优先级逐一检查规则，返回第一个匹配的 PluralForm。
    /// </summary>
    public sealed class PluralRuleSet
    {
        /// <summary>语言代码（如 "en"、"zh"、"ru"、"ar"）</summary>
        public string LanguageCode { get; }

        /// <summary>该语言支持的复数形式数量</summary>
        public int FormCount => _rules.Count;

        /// <summary>所有规则</summary>
        public IReadOnlyList<PluralRule> Rules => _rules;

        /// <summary>规则列表（按优先级排序：最先检查的在前）</summary>
        private readonly List<PluralRule> _rules = new();

        public PluralRuleSet(string languageCode)
        {
            LanguageCode = languageCode ?? throw new ArgumentNullException(nameof(languageCode));
        }

        /// <summary>添加规则（自动按优先级排序：Other 总是最后）</summary>
        public void AddRule(PluralRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            _rules.Add(rule);
            SortRules();
        }

        /// <summary>
        /// 根据数值确定复数形式。
        /// 按优先级逐一检查，返回第一个匹配的 PluralForm。
        /// 如果没有规则匹配（理论上不会发生，因为 Other 兜底），返回 Other。
        /// </summary>
        public PluralForm GetPluralForm(decimal number)
        {
            foreach (var rule in _rules)
            {
                if (rule.Matches(number))
                    return rule.Form;
            }
            return PluralForm.Other;
        }

        /// <summary>获取指定复数形式的规则</summary>
        public PluralRule? GetRule(PluralForm form)
        {
            return _rules.FirstOrDefault(r => r.Form == form);
        }

        private void SortRules()
        {
            // Other 总是排最后
            _rules.Sort((a, b) =>
            {
                if (a.Form == PluralForm.Other) return 1;
                if (b.Form == PluralForm.Other) return -1;
                return 0;
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // PluralRulesRegistry —— 全局复数规则注册中心
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 复数规则注册中心。管理所有语言的复数规则集。
    /// 内置 CLDR 标准的 30+ 种语言规则。
    /// </summary>
    public sealed class PluralRulesRegistry
    {
        private static readonly Lazy<PluralRulesRegistry> _lazy =
            new(() => new PluralRulesRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static PluralRulesRegistry Instance => _lazy.Value;

        /// <summary>语言代码 → 复数规则集</summary>
        private readonly Dictionary<string, PluralRuleSet> _ruleSets =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已注册的语言数量</summary>
        public int RegisteredCount => _ruleSets.Count;

        private PluralRulesRegistry()
        {
            RegisterBuiltinRules();
        }

        /// <summary>注册 CLDR 标准复数规则</summary>
        private void RegisterBuiltinRules()
        {
            // ── 仅 Other 形式的语言（中日韩泰越南等） ──
            RegisterSingleOther("zh", "Chinese");
            RegisterSingleOther("ja", "Japanese");
            RegisterSingleOther("ko", "Korean");
            RegisterSingleOther("th", "Thai");
            RegisterSingleOther("vi", "Vietnamese");
            RegisterSingleOther("id", "Indonesian");
            RegisterSingleOther("ms", "Malay");
            RegisterSingleOther("ka", "Georgian");
            RegisterSingleOther("zh-Hans", "Chinese (Simplified)");
            RegisterSingleOther("zh-Hant", "Chinese (Traditional)");
            RegisterSingleOther("lo", "Lao");
            RegisterSingleOther("my", "Burmese");

            // ── One/Other 形式的语言（英语、德语等） ──
            RegisterOneOther("en", "English", n => n == 1m);
            RegisterOneOther("de", "German", n => n == 1m);
            RegisterOneOther("es", "Spanish", n => n == 1m);
            RegisterOneOther("pt", "Portuguese", n => n == 1m);
            RegisterOneOther("it", "Italian", n => n == 1m);
            RegisterOneOther("nl", "Dutch", n => n == 1m);
            RegisterOneOther("sv", "Swedish", n => n == 1m);
            RegisterOneOther("da", "Danish", n => n == 1m);
            RegisterOneOther("fi", "Finnish", n => n == 1m);
            RegisterOneOther("el", "Greek", n => n == 1m);
            RegisterOneOther("he", "Hebrew", n => n == 1m);
            RegisterOneOther("bg", "Bulgarian", n => n == 1m);
            RegisterOneOther("eo", "Esperanto", n => n == 1m);
            RegisterOneOther("hu", "Hungarian", n => n == 1m);
            RegisterOneOther("no", "Norwegian", n => n == 1m);
            RegisterOneOther("tr", "Turkish", n => n == 1m);
            RegisterOneOther("nb", "Norwegian Bokmål", n => n == 1m);
            RegisterOneOther("bn", "Bengali", n => n == 1m);
            RegisterOneOther("gu", "Gujarati", n => n == 1m);
            RegisterOneOther("hi", "Hindi", n => n == 1m && (int)n != 0);
            RegisterOneOther("kn", "Kannada", n => n == 1m);
            RegisterOneOther("ml", "Malayalam", n => n == 1m);
            RegisterOneOther("mr", "Marathi", n => n == 1m);
            RegisterOneOther("or", "Odia", n => n == 1m);
            RegisterOneOther("pa", "Punjabi", n => n == 1m && (int)n != 0);
            RegisterOneOther("ta", "Tamil", n => n == 1m);
            RegisterOneOther("te", "Telugu", n => n == 1m);
            RegisterOneOther("ur", "Urdu", n => n == 1m);

            // ── One/Other 形式（特殊条件） ──
            // 法语: 0 和 1 都算 one
            RegisterOneOther("fr", "French", n => n == 0m || n == 1m);
            RegisterOneOther("pt-BR", "Portuguese (Brazil)", n => (int)n == 0 || (int)n == 1);

            // ── One/Few/Many/Other 形式（斯拉夫语系） ──
            // 俄语
            RegisterSlavic("ru", "Russian");
            // 乌克兰语
            RegisterSlavic("uk", "Ukrainian");

            // ── One/Few/Many/Other 形式（波兰语特殊规则） ──
            RegisterPolish("pl", "Polish");

            // ── One/Two/Few/Many/Other 形式（阿拉伯语） ──
            RegisterArabic("ar", "Arabic");

            // ── One/Two/Few/Other 形式（捷克语） ──
            RegisterCzech("cs", "Czech");

            // ── One/Two/Few/Other 形式（斯洛伐克语） ──
            RegisterCzech("sk", "Slovak");

            // ── One/Two/Three/Few/Other 形式（爱尔兰语） ──
            RegisterOneTwoFewOther("ga", "Irish");

            // ── One/Dual/Plural/Other 形式（希伯来语） ──
            RegisterHebrew("he", "Hebrew");

            // ── One/Few/Other 形式（罗马尼亚语） ──
            RegisterRomanian("ro", "Romanian");

            // ── One/Two/Other 形式（斯洛文尼亚语） ──
            RegisterSlovenian("sl", "Slovenian");

            // ── One/Few/Many/Other 形式（马其顿语） ──
            RegisterSlavic("mk", "Macedonian");

            // ── One/Other（波斯语） ──
            RegisterOneOther("fa", "Persian", n => (int)n == 1);

            // ── 特殊：立陶宛语 ──
            RegisterLithuanian("lt", "Lithuanian");

            // ── 特殊：拉脱维亚语 ──
            RegisterLatvian("lv", "Latvian");

            // ── 特殊：冰岛语 ──
            RegisterIcelandic("is", "Icelandic");
        }

        // ── 辅助注册方法 ──

        private void RegisterSingleOther(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true,
                $"Other: all"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterOneOther(string langCode, string langName,
                                       Func<decimal, bool> oneCondition)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One, oneCondition,
                $"One: n = 1"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true,
                "Other: all other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterSlavic(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 == 1 && mod100 != 11;
                },
                "One: i = 1 and v = 0 and i % 10 != 11"));

            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20);
                },
                "Few: v = 0 and i % 10 = 2..4 and i % 100 != 12..14"));

            ruleSet.AddRule(new PluralRule(PluralForm.Many,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 == 0 || mod10 >= 5 && mod10 <= 9 ||
                           (mod100 >= 11 && mod100 <= 14);
                },
                "Many: v = 0 and i % 10 = 0 or v = 0 and i % 10 = 5..9 or v = 0 and i % 100 = 11..14"));

            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterPolish(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)n == 1, "One: i = 1 and v = 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int v = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return v >= 2 && v <= 4 && (mod100 < 12 || mod100 > 14);
                },
                "Few: v = 2..4 and i % 100 != 12..14"));
            ruleSet.AddRule(new PluralRule(PluralForm.Many,
                n =>
                {
                    int v = (int)Math.Abs(n);
                    int mod10 = v % 10;
                    int mod100 = v % 100;
                    return (v != 1 && v % 10 >= 0 && v % 10 <= 1) ||
                           (mod10 >= 5 && mod10 <= 9) ||
                           (mod100 >= 12 && mod100 <= 14);
                },
                "Many: others"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterArabic(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.Zero,
                n => n == 0m, "Zero: n = 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => n == 1m, "One: n = 1"));
            ruleSet.AddRule(new PluralRule(PluralForm.Two,
                n => n == 2m, "Two: n = 2"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod100 >= 3 && mod100 <= 10;
                },
                "Few: n % 100 = 3..10"));
            ruleSet.AddRule(new PluralRule(PluralForm.Many,
                n =>
                {
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod100 >= 11 && mod100 <= 99;
                },
                "Many: n % 100 = 11..99"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterCzech(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)n == 1, "One: i = 1 and v = 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int v = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return v >= 2 && v <= 4 && (mod100 < 12 || mod100 > 14);
                },
                "Few: i % 10 = 2..4 and i % 100 != 12..14"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterHebrew(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)n == 1 && (int)n != 0, "One: i = 1 and i != 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.Two,
                n => (int)n == 2, "Two: i = 2"));
            ruleSet.AddRule(new PluralRule(PluralForm.Many,
                n =>
                {
                    int v = (int)Math.Abs(n);
                    return v != 0 && v != 1 && v % 10 != 0;
                },
                "Many: not 0..10 mod 100"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterRomanian(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)n == 1, "One: i = 1 and v = 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int v = (int)Math.Abs(n);
                    return v != 0 && v % 100 >= 1 && v % 100 <= 19;
                },
                "Few: n != 1 and n % 100 = 1..19"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterSlovenian(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)Math.Abs(n) % 100 == 1, "One: v % 100 = 1"));
            ruleSet.AddRule(new PluralRule(PluralForm.Two,
                n => (int)Math.Abs(n) % 100 == 2, "Two: v % 100 = 2"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int mod = (int)Math.Abs(n) % 100;
                    return mod >= 3 && mod <= 4;
                },
                "Few: v % 100 = 3..4"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterOneTwoFewOther(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n => (int)n == 1, "One: n = 1"));
            ruleSet.AddRule(new PluralRule(PluralForm.Two,
                n => (int)n == 2, "Two: n = 2"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n => (int)n >= 3 && (int)n <= 6, "Few: n = 3..6"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterLithuanian(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 == 1 && mod100 != 11;
                },
                "One: n % 10 = 1 and n % 100 != 11"));
            ruleSet.AddRule(new PluralRule(PluralForm.Few,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 >= 2 && mod10 <= 9 && (mod100 < 11 || mod100 > 19);
                },
                "Few: n % 10 = 2..9 and n % 100 not 11..19"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterLatvian(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.Zero,
                n => (int)n == 0, "Zero: n % 10 = 0 or n = 0"));
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 == 1 && mod100 != 11;
                },
                "One: n % 10 = 1 and n % 100 != 11"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        private void RegisterIcelandic(string langCode, string langName)
        {
            var ruleSet = new PluralRuleSet(langCode);
            ruleSet.AddRule(new PluralRule(PluralForm.One,
                n =>
                {
                    int mod10 = (int)Math.Abs(n) % 10;
                    int mod100 = (int)Math.Abs(n) % 100;
                    return mod10 == 1 && mod100 != 11 || (int)Math.Abs(n) % 1000 == 0;
                },
                "One: special Icelandic rules"));
            ruleSet.AddRule(new PluralRule(PluralForm.Other, _ => true, "Other"));
            _ruleSets[langCode] = ruleSet;
        }

        // ── 公开 API ──

        /// <summary>注册自定义复数规则集</summary>
        public void RegisterRuleSet(PluralRuleSet ruleSet)
        {
            if (ruleSet == null) throw new ArgumentNullException(nameof(ruleSet));
            _ruleSets[ruleSet.LanguageCode] = ruleSet;
        }

        /// <summary>获取指定语言的复数规则集</summary>
        public PluralRuleSet? GetRuleSet(string languageCode)
        {
            _ruleSets.TryGetValue(languageCode, out var ruleSet);
            return ruleSet;
        }

        /// <summary>判断某语言是否已注册复数规则</summary>
        public bool HasRuleSet(string languageCode) => _ruleSets.ContainsKey(languageCode);

        /// <summary>
        /// 快速获取复数形式。根据语言代码和数值返回 PluralForm。
        /// </summary>
        public PluralForm GetPluralForm(string languageCode, decimal number)
        {
            if (_ruleSets.TryGetValue(languageCode, out var ruleSet))
                return ruleSet.GetPluralForm(number);

            // 默认：英语式 One/Other
            return number == 1m ? PluralForm.One : PluralForm.Other;
        }

        /// <summary>获取所有已注册的语言代码</summary>
        public IEnumerable<string> GetRegisteredLanguages() => _ruleSets.Keys;

        /// <summary>获取所有已注册的规则集</summary>
        public IEnumerable<PluralRuleSet> GetAllRuleSets() => _ruleSets.Values;
    }

    // ══════════════════════════════════════════════════════════════════
    // PluralFormatter —— 复数字符串格式化器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 复数字符串格式化器。根据数量和语言自动选择正确的复数形式文本。
    /// 用法：
    ///   formatter.Set("one", "{count} apple");
    ///   formatter.Set("other", "{count} apples");
    ///   formatter.Format(5, "en") → "5 apples"
    /// </summary>
    public sealed class PluralFormatter
    {
        /// <summary>各复数形式对应的模板文本</summary>
        private readonly Dictionary<PluralForm, string> _templates = new();

        /// <summary>设置指定复数形式的模板</summary>
        public void Set(PluralForm form, string template)
        {
            if (!string.IsNullOrEmpty(template))
                _templates[form] = template;
        }

        /// <summary>设置 Other 形式模板</summary>
        public void SetOther(string template) => Set(PluralForm.Other, template);

        /// <summary>设置 One 形式模板</summary>
        public void SetOne(string template) => Set(PluralForm.One, template);

        /// <summary>
        /// 格式化复数字符串。
        /// 根据 count 和 languageCode 选择正确的复数形式模板，
        /// 并将 {count} 替换为实际数值。
        /// </summary>
        public string Format(decimal count, string languageCode,
                             params (string Name, object Value)[] extraArgs)
        {
            var registry = PluralRulesRegistry.Instance;
            var form = registry.GetPluralForm(languageCode, count);

            if (_templates.TryGetValue(form, out var template))
            {
                var result = template.Replace("{count}", count.ToString());
                result = result.Replace("{Count}", count.ToString());
                result = result.Replace("{n}", count.ToString());

                foreach (var (name, value) in extraArgs)
                {
                    result = result.Replace($"{{{name}}}", value?.ToString() ?? "");
                }
                return result;
            }

            // 回退到 Other
            if (_templates.TryGetValue(PluralForm.Other, out var otherTemplate))
            {
                var result = otherTemplate.Replace("{count}", count.ToString());
                return result;
            }

            return count.ToString();
        }

        /// <summary>从字典批量设置模板</summary>
        public void SetTemplates(Dictionary<string, string> templates)
        {
            if (templates == null) return;
            foreach (var kvp in templates)
            {
                if (Enum.TryParse<PluralForm>(kvp.Key, true, out var form))
                {
                    _templates[form] = kvp.Value;
                }
            }
        }

        /// <summary>获取所有已设置的模板</summary>
        public IReadOnlyDictionary<PluralForm, string> GetTemplates() => _templates;
    }
}
