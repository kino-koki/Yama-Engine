using System;
using System.Collections.Generic;
using System.Linq;

namespace ShanyuEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 本地化编辑器系统（LocalizationEditorSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现引擎编辑器内的本地化工具集，类似 UE Localization Dashboard。
    //   1. LocalizationDashboard：总览面板
    //   2. TranslationWorkspace：翻译工作区
    //   3. TranslationMemory：翻译记忆库
    //   4. IMachineTranslator：机器翻译接口
    //   5. Glossary：术语表
    //   6. TranslationApproval：审批流程
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 审批状态枚举。
    /// </summary>
    public enum ApprovalState
    {
        /// <summary>等待翻译</summary>
        PendingTranslation,
        /// <summary>翻译完成，等待审核</summary>
        PendingReview,
        /// <summary>审核完成，等待批准</summary>
        PendingApproval,
        /// <summary>已批准，可发布</summary>
        Approved,
        /// <summary>已拒绝/退回</summary>
        Rejected
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizationDashboard —— 总览面板
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化总览面板。显示项目本地化的整体状态，
    /// 包括语言列表、翻译进度、覆盖率、缺失字符串等。
    /// 类似 UE Localization Dashboard 的总览视图。
    /// </summary>
    public sealed class LocalizationDashboard
    {
        /// <summary>仪表板数据刷新事件</summary>
        public event EventHandler? DataRefreshed;

        /// <summary>语言概览列表</summary>
        private List<LocaleOverview> _localeOverviews = new();

        /// <summary>所有语言概览</summary>
        public IReadOnlyList<LocaleOverview> LocaleOverviews => _localeOverviews;

        /// <summary>项目总字符串数（以源语言表为基准）</summary>
        public int TotalStringCount { get; private set; }

        /// <summary>项目支持的语言数</summary>
        public int SupportedLanguageCount => _localeOverviews.Count;

        /// <summary>项目整体覆盖率（所有语言的加权平均）</summary>
        public float OverallCoverage { get; private set; }

        /// <summary>总缺失字符串数（跨所有语言）</summary>
        public int TotalMissingStrings { get; private set; }

        /// <summary>总关键缺失字符串数</summary>
        public int TotalCriticalMissing { get; private set; }

        /// <summary>
        /// 刷新仪表板数据。
        /// 从 StringTableRegistry 收集所有集合的覆盖率数据并汇总。
        /// </summary>
        public void Refresh()
        {
            var registry = StringTableRegistry.Instance;
            _localeOverviews.Clear();

            // 收集所有语言标签
            var allLocaleTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in registry.GetAllCollections())
            {
                foreach (var tag in collection.LoadedLocaleTags)
                    allLocaleTags.Add(tag);
            }

            // 为每个语言计算概览
            foreach (var tag in allLocaleTags)
            {
                int totalKeys = 0;
                int translated = 0;
                int missing = 0;
                int criticalMissing = 0;

                foreach (var collection in registry.GetAllCollections())
                {
                    var report = collection.GetCoverageReport(tag);
                    totalKeys += report.TotalKeys;
                    translated += report.TranslatedCount;
                    missing += report.MissingCount;
                    criticalMissing += report.CriticalMissingCount;
                }

                float coverage = totalKeys > 0 ? (float)translated / totalKeys : 1.0f;

                _localeOverviews.Add(new LocaleOverview
                {
                    LocaleTag = tag,
                    TotalKeys = totalKeys,
                    TranslatedCount = translated,
                    MissingCount = missing,
                    CriticalMissingCount = criticalMissing,
                    Coverage = coverage
                });
            }

            // 汇总统计
            TotalStringCount = _localeOverviews.Count > 0
                ? _localeOverviews.Max(o => o.TotalKeys) : 0;

            TotalMissingStrings = _localeOverviews.Sum(o => o.MissingCount);
            TotalCriticalMissing = _localeOverviews.Sum(o => o.CriticalMissingCount);

            OverallCoverage = _localeOverviews.Count > 0
                ? _localeOverviews.Average(o => o.Coverage) : 1.0f;

            DataRefreshed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>获取覆盖率最低的语言</summary>
        public LocaleOverview? GetLowestCoverageLocale()
        {
            return _localeOverviews.OrderBy(o => o.Coverage).FirstOrDefault();
        }

        /// <summary>获取缺少关键字符串的语言列表</summary>
        public IEnumerable<LocaleOverview> GetLocalesWithCriticalMissing()
        {
            return _localeOverviews.Where(o => o.CriticalMissingCount > 0);
        }
    }

    /// <summary>语言概览数据</summary>
    public sealed class LocaleOverview
    {
        public string LocaleTag { get; set; } = "";
        public int TotalKeys { get; set; }
        public int TranslatedCount { get; set; }
        public int MissingCount { get; set; }
        public int CriticalMissingCount { get; set; }
        public float Coverage { get; set; }
        public float CoveragePercent => Coverage * 100f;

        public override string ToString() =>
            $"[{LocaleTag}] {CoveragePercent:F1}% ({TranslatedCount}/{TotalKeys})";
    }

    // ══════════════════════════════════════════════════════════════════
    // TranslationWorkspace —— 翻译工作区
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 翻译工作区。提供逐条翻译的工作环境，
    /// 支持筛选未翻译、搜索、批量操作等。
    /// </summary>
    public sealed class TranslationWorkspace
    {
        /// <summary>当前翻译的语言标签</summary>
        public string TargetLocale { get; private set; }

        /// <summary>当前工作的字符串集合</summary>
        public StringTableCollection? CurrentCollection { get; private set; }

        /// <summary>当前筛选的条目列表</summary>
        private List<StringTableEntry> _filteredEntries = new();

        /// <summary>当前条目索引</summary>
        private int _currentIndex = -1;

        /// <summary>筛选器</summary>
        public TranslationFilter Filter { get; set; } = new();

        /// <summary>翻译修改事件</summary>
        public event EventHandler<(string Key, string NewValue)>? TranslationChanged;

        public TranslationWorkspace(string targetLocale)
        {
            TargetLocale = targetLocale ?? throw new ArgumentNullException(nameof(targetLocale));
        }

        /// <summary>加载字符串集合</summary>
        public void LoadCollection(StringTableCollection collection)
        {
            CurrentCollection = collection ?? throw new ArgumentNullException(nameof(collection));
            ApplyFilter();
        }

        /// <summary>应用筛选器</summary>
        public void ApplyFilter()
        {
            _filteredEntries.Clear();
            _currentIndex = -1;

            if (CurrentCollection == null) return;

            var table = CurrentCollection.GetTable(TargetLocale);
            if (table == null) return;

            IEnumerable<StringTableEntry> entries = table.Entries;

            // 状态筛选
            if (Filter.StateFilter.HasValue)
                entries = entries.Where(e => e.State == Filter.StateFilter.Value);

            // 仅未翻译
            if (Filter.UntranslatedOnly)
                entries = entries.Where(e =>
                    string.IsNullOrEmpty(e.Value) ||
                    e.State == StringEntryState.Untranslated);

            // 关键字符串
            if (Filter.CriticalOnly)
                entries = entries.Where(e => e.IsCritical);

            // 搜索过滤
            if (!string.IsNullOrEmpty(Filter.SearchPattern))
            {
                var pattern = Filter.SearchPattern.ToLowerInvariant();
                entries = entries.Where(e =>
                    e.Key.ToLowerInvariant().Contains(pattern) ||
                    e.Value.ToLowerInvariant().Contains(pattern));
            }

            _filteredEntries = entries.ToList();
        }

        /// <summary>筛选后条目数量</summary>
        public int FilteredCount => _filteredEntries.Count;

        /// <summary>导航到下一条</summary>
        public bool MoveNext()
        {
            if (_currentIndex < _filteredEntries.Count - 1)
            {
                _currentIndex++;
                return true;
            }
            return false;
        }

        /// <summary>导航到上一条</summary>
        public bool MovePrevious()
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                return true;
            }
            return false;
        }

        /// <summary>获取当前条目</summary>
        public StringTableEntry? CurrentEntry =>
            _currentIndex >= 0 && _currentIndex < _filteredEntries.Count
                ? _filteredEntries[_currentIndex]
                : null;

        /// <summary>获取源语言文本（用于参考对照）</summary>
        public string? GetSourceText(string key)
        {
            return CurrentCollection?.GetLocalizedString(
                CurrentCollection.SourceLocaleTag, key);
        }

        /// <summary>设置当前条目的翻译</summary>
        public void SetTranslation(string value, string? translator = null)
        {
            var entry = CurrentEntry;
            if (entry == null || CurrentCollection == null) return;

            var table = CurrentCollection.GetTable(TargetLocale);
            if (table == null) return;

            entry.Value = value;
            entry.State = StringEntryState.Translated;
            entry.LastModified = DateTime.UtcNow;
            entry.ModifiedBy = translator;
            table.Set(entry);

            TranslationChanged?.Invoke(this, (entry.Key, value));
        }

        /// <summary>跳转到指定索引</summary>
        public bool JumpTo(int index)
        {
            if (index >= 0 && index < _filteredEntries.Count)
            {
                _currentIndex = index;
                return true;
            }
            return false;
        }
    }

    /// <summary>翻译筛选器</summary>
    public sealed class TranslationFilter
    {
        /// <summary>按状态筛选（null = 不过滤）</summary>
        public StringEntryState? StateFilter { get; set; }

        /// <summary>仅显示未翻译</summary>
        public bool UntranslatedOnly { get; set; }

        /// <summary>仅显示关键字符串</summary>
        public bool CriticalOnly { get; set; }

        /// <summary>搜索关键词</summary>
        public string? SearchPattern { get; set; }

        /// <summary>按 Key 前缀筛选</summary>
        public string? KeyPrefix { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════
    // TranslationMemory —— 翻译记忆库
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 翻译记忆库（Translation Memory, TM）。
    /// 缓存已翻译的短语对（源语言→目标语言），
    /// 在翻译新字符串时自动搜索相似的历史翻译并建议。
    /// </summary>
    public sealed class TranslationMemory
    {
        private static readonly Lazy<TranslationMemory> _lazy =
            new(() => new TranslationMemory(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static TranslationMemory Instance => _lazy.Value;

        /// <summary>最小匹配相似度阈值（0.0 - 1.0）</summary>
        public float MinSimilarityThreshold { get; set; } = 0.6f;

        /// <summary>最大返回建议数</summary>
        public int MaxSuggestions { get; set; } = 5;

        /// <summary>翻译记忆条目</summary>
        private readonly List<TranslationMemoryEntry> _entries = new();

        /// <summary>总条目数</summary>
        public int Count => _entries.Count;

        private TranslationMemory() { }

        /// <summary>添加一条翻译记忆</summary>
        public void AddEntry(string sourceText, string translatedText,
                            string sourceLocale, string targetLocale,
                            string? context = null)
        {
            _entries.Add(new TranslationMemoryEntry
            {
                SourceText = sourceText ?? "",
                TranslatedText = translatedText ?? "",
                SourceLocale = sourceLocale ?? "",
                TargetLocale = targetLocale ?? "",
                Context = context,
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// 搜索相似的翻译记忆（模糊匹配）。
        /// 使用 Levenshtein 距离计算相似度。
        /// </summary>
        public List<TranslationMemorySuggestion> FindSuggestions(
            string sourceText, string sourceLocale, string targetLocale)
        {
            if (string.IsNullOrEmpty(sourceText)) return new List<TranslationMemorySuggestion>();

            var matches = _entries
                .Where(e => string.Equals(e.SourceLocale, sourceLocale, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(e.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase))
                .Select(e => new
                {
                    Entry = e,
                    Similarity = ComputeSimilarity(sourceText, e.SourceText)
                })
                .Where(m => m.Similarity >= MinSimilarityThreshold)
                .OrderByDescending(m => m.Similarity)
                .Take(MaxSuggestions)
                .Select(m => new TranslationMemorySuggestion
                {
                    SourceText = m.Entry.SourceText,
                    TranslatedText = m.Entry.TranslatedText,
                    Similarity = m.Similarity,
                    Context = m.Entry.Context,
                    Timestamp = m.Entry.Timestamp
                })
                .ToList();

            return matches;
        }

        /// <summary>精确匹配</summary>
        public string? FindExactMatch(string sourceText, string sourceLocale, string targetLocale)
        {
            return _entries.FirstOrDefault(e =>
                string.Equals(e.SourceText, sourceText, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.SourceLocale, sourceLocale, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.TargetLocale, targetLocale, StringComparison.OrdinalIgnoreCase))?.TranslatedText;
        }

        /// <summary>从字符串集合批量导入翻译记忆</summary>
        public void ImportFromStringTableCollection(StringTableCollection collection,
                                                     string sourceLocale, string targetLocale)
        {
            var sourceTable = collection.GetTable(sourceLocale);
            var targetTable = collection.GetTable(targetLocale);
            if (sourceTable == null || targetTable == null) return;

            foreach (var entry in sourceTable.Entries)
            {
                var translation = targetTable.Get(entry.Key);
                if (!string.IsNullOrEmpty(translation))
                {
                    AddEntry(entry.Value, translation, sourceLocale, targetLocale, entry.Context);
                }
            }
        }

        /// <summary>计算两个字符串的相似度（0.0 - 1.0）</summary>
        private static float ComputeSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0f;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;

            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1.0f;

            int distance = LevenshteinDistance(a, b);
            return 1.0f - (float)distance / maxLen;
        }

        /// <summary>Levenshtein 编辑距离</summary>
        private static int LevenshteinDistance(string a, string b)
        {
            int m = a.Length;
            int n = b.Length;

            var dp = new int[m + 1, n + 1];
            for (int i = 0; i <= m; i++) dp[i, 0] = i;
            for (int j = 0; j <= n; j++) dp[0, j] = j;

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            }

            return dp[m, n];
        }
    }

    /// <summary>翻译记忆条目</summary>
    public sealed class TranslationMemoryEntry
    {
        public string SourceText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        public string SourceLocale { get; set; } = "";
        public string TargetLocale { get; set; } = "";
        public string? Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>翻译记忆建议</summary>
    public sealed class TranslationMemorySuggestion
    {
        public string SourceText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        public float Similarity { get; set; }
        public string? Context { get; set; }
        public DateTime Timestamp { get; set; }
        public float SimilarityPercent => Similarity * 100f;

        public override string ToString() =>
            $"[{SimilarityPercent:F0}%] {TranslatedText}";
    }

    // ══════════════════════════════════════════════════════════════════
    // IMachineTranslator —— 机器翻译接口
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 机器翻译接口。预留翻译服务接口，
    /// 支持 Google Translate、DeepL、本地模型等多种后端。
    /// </summary>
    public interface IMachineTranslator
    {
        /// <summary>翻译服务名称</summary>
        string ServiceName { get; }

        /// <summary>是否已配置/可用</summary>
        bool IsAvailable { get; }

        /// <summary>翻译单条文本</summary>
        string Translate(string text, string sourceLocale, string targetLocale);

        /// <summary>批量翻译</summary>
        List<string> TranslateBatch(List<string> texts, string sourceLocale, string targetLocale);

        /// <summary>检测文本语言</summary>
        string DetectLanguage(string text);
    }

    /// <summary>
    /// 内置占位机器翻译器（用于编辑器原型测试）。
    /// 直接返回源文本 + 语言标签作为占位。
    /// </summary>
    public sealed class PlaceholderTranslator : IMachineTranslator
    {
        public string ServiceName => "Placeholder";
        public bool IsAvailable => true;

        public string Translate(string text, string sourceLocale, string targetLocale)
        {
            return $"[{targetLocale}] {text}";
        }

        public List<string> TranslateBatch(List<string> texts, string sourceLocale, string targetLocale)
        {
            return texts.Select(t => Translate(t, sourceLocale, targetLocale)).ToList();
        }

        public string DetectLanguage(string text)
        {
            return "en-US"; // 占位
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Glossary —— 术语表
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 术语表（Glossary）。定义项目中专有名词的强制翻译，
    /// 确保一致性和正确性。例如角色名、地名、技能名等。
    /// </summary>
    public sealed class Glossary
    {
        private static readonly Lazy<Glossary> _lazy =
            new(() => new Glossary(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static Glossary Instance => _lazy.Value;

        /// <summary>术语条目：原词（源语言） → GlossaryTerm</summary>
        private readonly Dictionary<string, GlossaryTerm> _terms =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>术语数量</summary>
        public int Count => _terms.Count;

        /// <summary>术语添加事件</summary>
        public event EventHandler<GlossaryTerm>? TermAdded;
        public event EventHandler<GlossaryTerm>? TermRemoved;

        private Glossary() { }

        /// <summary>添加术语</summary>
        public void AddTerm(GlossaryTerm term)
        {
            if (term == null || string.IsNullOrEmpty(term.SourceTerm))
                throw new ArgumentException("术语或原词不能为空");
            _terms[term.SourceTerm] = term;
            TermAdded?.Invoke(this, term);
        }

        /// <summary>移除术语</summary>
        public bool RemoveTerm(string sourceTerm)
        {
            if (_terms.TryGetValue(sourceTerm, out var term) && _terms.Remove(sourceTerm))
            {
                TermRemoved?.Invoke(this, term);
                return true;
            }
            return false;
        }

        /// <summary>获取术语</summary>
        public GlossaryTerm? GetTerm(string sourceTerm)
        {
            _terms.TryGetValue(sourceTerm, out var term);
            return term;
        }

        /// <summary>
        /// 获取术语在目标语言中的翻译。
        /// </summary>
        public string? GetTranslation(string sourceTerm, string targetLocale)
        {
            if (_terms.TryGetValue(sourceTerm, out var term))
            {
                term.Translations.TryGetValue(targetLocale, out var translation);
                return translation;
            }
            return null;
        }

        /// <summary>获取所有术语</summary>
        public IEnumerable<GlossaryTerm> GetAllTerms() => _terms.Values;

        /// <summary>检查文本中是否包含术语</summary>
        public IEnumerable<GlossaryMatch> FindTermsInText(string text, string targetLocale)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<GlossaryMatch>();

            var matches = new List<GlossaryMatch>();
            foreach (var term in _terms.Values)
            {
                int index = 0;
                while ((index = text.IndexOf(term.SourceTerm, index,
                           StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var translation = GetTranslation(term.SourceTerm, targetLocale);
                    matches.Add(new GlossaryMatch
                    {
                        Term = term,
                        Position = index,
                        Length = term.SourceTerm.Length,
                        SuggestedTranslation = translation
                    });
                    index += term.SourceTerm.Length;
                }
            }
            return matches.OrderBy(m => m.Position);
        }

        /// <summary>一致性检查：检查翻译中术语是否被正确使用</summary>
        public List<GlossaryConsistencyIssue> CheckConsistency(
            string sourceText, string translatedText, string sourceLocale, string targetLocale)
        {
            var issues = new List<GlossaryConsistencyIssue>();

            foreach (var term in _terms.Values)
            {
                if (sourceText.IndexOf(term.SourceTerm, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var expectedTranslation = GetTranslation(term.SourceTerm, targetLocale);
                if (string.IsNullOrEmpty(expectedTranslation))
                {
                    issues.Add(new GlossaryConsistencyIssue
                    {
                        Term = term.SourceTerm,
                        IssueType = GlossaryIssueType.MissingTranslation,
                        Description = $"术语 '{term.SourceTerm}' 缺少 {targetLocale} 翻译"
                    });
                    continue;
                }

                if (translatedText.IndexOf(expectedTranslation, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    issues.Add(new GlossaryConsistencyIssue
                    {
                        Term = term.SourceTerm,
                        IssueType = GlossaryIssueType.InconsistentTranslation,
                        Description = $"术语 '{term.SourceTerm}' 应翻译为 '{expectedTranslation}'，但未在译文中找到"
                    });
                }
            }

            return issues;
        }
    }

    /// <summary>术语条目</summary>
    public sealed class GlossaryTerm
    {
        /// <summary>原词（源语言）</summary>
        public string SourceTerm { get; set; } = "";

        /// <summary>各语言翻译：localeTag → 翻译</summary>
        public Dictionary<string, string> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>术语类别（角色名/地名/技能名/UI术语等）</summary>
        public string Category { get; set; } = "";

        /// <summary>术语说明（供译者参考）</summary>
        public string? Description { get; set; }

        /// <summary>是否为强制术语（必须在翻译中一致使用）</summary>
        public bool IsMandatory { get; set; } = true;

        /// <summary>是否区分大小写</summary>
        public bool CaseSensitive { get; set; } = false;
    }

    /// <summary>术语匹配结果</summary>
    public sealed class GlossaryMatch
    {
        public GlossaryTerm Term { get; set; } = null!;
        public int Position { get; set; }
        public int Length { get; set; }
        public string? SuggestedTranslation { get; set; }
    }

    /// <summary>术语一致性检查问题</summary>
    public sealed class GlossaryConsistencyIssue
    {
        public string Term { get; set; } = "";
        public GlossaryIssueType IssueType { get; set; }
        public string Description { get; set; } = "";

        public override string ToString() => $"[{IssueType}] {Description}";
    }

    /// <summary>术语问题类型</summary>
    public enum GlossaryIssueType
    {
        /// <summary>缺少翻译</summary>
        MissingTranslation,
        /// <summary>翻译不一致</summary>
        InconsistentTranslation,
        /// <summary>术语错误使用</summary>
        MisusedTerm
    }

    // ══════════════════════════════════════════════════════════════════
    // TranslationApproval —— 审批流程
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 审批流程管理。实现翻译→审核→批准→发布的完整工作流。
    /// </summary>
    public sealed class TranslationApproval
    {
        /// <summary>审批记录</summary>
        private readonly List<ApprovalRecord> _records = new();

        /// <summary>翻译提交事件</summary>
        public event EventHandler<ApprovalRecord>? TranslationSubmitted;
        /// <summary>审核事件</summary>
        public event EventHandler<ApprovalRecord>? ReviewCompleted;
        /// <summary>批准事件</summary>
        public event EventHandler<ApprovalRecord>? ApprovalCompleted;
        /// <summary>拒绝事件</summary>
        public event EventHandler<ApprovalRecord>? Rejected;

        /// <summary>提交翻译（进入 PendingReview 状态）</summary>
        public ApprovalRecord SubmitTranslation(string collectionId, string key,
                                                string localeTag, string translation,
                                                string translator, string? comment = null)
        {
            var record = new ApprovalRecord
            {
                CollectionId = collectionId,
                Key = key,
                LocaleTag = localeTag,
                Translation = translation,
                Translator = translator,
                State = ApprovalState.PendingReview,
                SubmittedAt = DateTime.UtcNow,
                Comment = comment
            };
            _records.Add(record);
            TranslationSubmitted?.Invoke(this, record);
            return record;
        }

        /// <summary>审核翻译（通过 → PendingApproval）</summary>
        public ApprovalRecord Review(string recordId, string reviewer,
                                     bool approved, string? reviewComment = null)
        {
            var record = _records.FirstOrDefault(r => r.RecordId == recordId);
            if (record == null) throw new ArgumentException($"审批记录不存在: {recordId}");

            record.Reviewer = reviewer;
            record.ReviewedAt = DateTime.UtcNow;
            record.ReviewComment = reviewComment;

            if (approved)
            {
                record.State = ApprovalState.PendingApproval;
                ReviewCompleted?.Invoke(this, record);
            }
            else
            {
                record.State = ApprovalState.Rejected;
                record.RejectionReason = reviewComment;
                Rejected?.Invoke(this, record);
            }

            return record;
        }

        /// <summary>批准翻译（可发布）</summary>
        public ApprovalRecord Approve(string recordId, string approver,
                                        string? approvalComment = null)
        {
            var record = _records.FirstOrDefault(r => r.RecordId == recordId);
            if (record == null) throw new ArgumentException($"审批记录不存在: {recordId}");

            record.Approver = approver;
            record.ApprovedAt = DateTime.UtcNow;
            record.State = ApprovalState.Approved;
            record.ApprovalComment = approvalComment;
            ApprovalCompleted?.Invoke(this, record);

            return record;
        }

        /// <summary>获取所有待审核的记录</summary>
        public IEnumerable<ApprovalRecord> GetPendingReviews()
        {
            return _records.Where(r => r.State == ApprovalState.PendingReview);
        }

        /// <summary>获取所有待批准的记录</summary>
        public IEnumerable<ApprovalRecord> GetPendingApprovals()
        {
            return _records.Where(r => r.State == ApprovalState.PendingApproval);
        }

        /// <summary>获取所有记录</summary>
        public IReadOnlyList<ApprovalRecord> GetAllRecords() => _records;
    }

    /// <summary>审批记录</summary>
    public sealed class ApprovalRecord
    {
        public string RecordId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string CollectionId { get; set; } = "";
        public string Key { get; set; } = "";
        public string LocaleTag { get; set; } = "";
        public string Translation { get; set; } = "";
        public string Translator { get; set; } = "";
        public string? Reviewer { get; set; }
        public string? Approver { get; set; }
        public ApprovalState State { get; set; } = ApprovalState.PendingTranslation;
        public DateTime SubmittedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public string? Comment { get; set; }
        public string? ReviewComment { get; set; }
        public string? ApprovalComment { get; set; }
        public string? RejectionReason { get; set; }
    }
}
