using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShanyuEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 字符串表系统（StringTableSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现类似 UE String Table / Unity String Table Collection 的字符串表系统。
    //   1. StringTableEntry：单条本地化字符串（含 Key/翻译/复数/性别变体/元数据）
    //   2. StringTable：单语言字符串表（Key → StringTableEntry 映射）
    //   3. StringTableCollection：多语言字符串集合（TableID → 各语言 StringTable）
    //   4. 多格式加载（CSV/JSON/PO/RESX）
    //   5. 字符串查找（GetLocalizedString）
    //   6. 参数格式化（{0}位置参数 / {Name}命名参数 / 复数 / 性别）
    //   7. 字符串编辑（添加/修改/删除/搜索/批量导入导出）
    //   8. 缺失翻译报告与覆盖率统计
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 字符串表条目状态（用于翻译工作流跟踪）。
    /// </summary>
    public enum StringEntryState
    {
        /// <summary>未翻译</summary>
        Untranslated,
        /// <summary>翻译中（草稿）</summary>
        Draft,
        /// <summary>已翻译（待审核）</summary>
        Translated,
        /// <summary>已审核（待批准）</summary>
        Reviewed,
        /// <summary>已批准（可发布）</summary>
        Approved,
        /// <summary>需要更新（源字符串已修改，翻译需同步）</summary>
        NeedsUpdate
    }

    /// <summary>
    /// 字符串表条目。包含 Key、翻译文本、复数形式、性别变体、
    /// 译者备注、状态等完整信息。
    /// </summary>
    public sealed class StringTableEntry
    {
        /// <summary>字符串键（唯一标识符，如 "UI.MainMenu.Start"）</summary>
        public string Key { get; set; } = "";

        /// <summary>翻译文本（主文本 / 默认形式）</summary>
        public string Value { get; set; } = "";

        /// <summary>复数形式文本。索引映射到 PluralForm 枚举。</summary>
        public Dictionary<int, string> PluralForms { get; set; } = new();

        /// <summary>男性变体文本</summary>
        public string? MasculineValue { get; set; }

        /// <summary>女性变体文本</summary>
        public string? FeminineValue { get; set; }

        /// <summary>中性变体文本</summary>
        public string? NeutralValue { get; set; }

        /// <summary>译者备注（不显示给玩家）</summary>
        public string? Comment { get; set; }

        /// <summary>条目状态</summary>
        public StringEntryState State { get; set; } = StringEntryState.Untranslated;

        /// <summary>上下文信息（帮助译者理解上下文）</summary>
        public string? Context { get; set; }

        /// <summary>最后修改时间</summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>最后修改者</summary>
        public string? ModifiedBy { get; set; }

        /// <summary>是否已标记为关键字符串（必须翻译）</summary>
        public bool IsCritical { get; set; }

        /// <summary>是否支持富文本/Markdown</summary>
        public bool IsRichText { get; set; }

        /// <summary>字符串来源（代码/蓝图/资产等）</summary>
        public string? Source { get; set; }

        /// <summary>获取用于格式化前的原始模板文本</summary>
        public string Template => Value;

        public override string ToString() => $"[{Key}] = {Value}";
    }

    /// <summary>
    /// 字符串表。保存某种语言下的所有本地化条目，
    /// 以 Key → StringTableEntry 的映射存储。
    /// 类似 UE 的 String Table / Unity 的 String Table。
    /// </summary>
    public sealed class StringTable
    {
        /// <summary>字符串表唯一标识</summary>
        public string TableId { get; }

        /// <summary>所属语言标签</summary>
        public string LocaleTag { get; }

        /// <summary>字符串表显示名称</summary>
        public string DisplayName { get; set; }

        /// <summary>字符串表描述</summary>
        public string? Description { get; set; }

        /// <summary>条目数量</summary>
        public int Count => _entries.Count;

        /// <summary>所有键</summary>
        public IEnumerable<string> Keys => _entries.Keys;

        /// <summary>所有条目</summary>
        public IEnumerable<StringTableEntry> Entries => _entries.Values;

        private readonly Dictionary<string, StringTableEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>字符串表修改事件</summary>
        public event EventHandler<StringTableEntry>? EntryAdded;
        public event EventHandler<(string Key, StringTableEntry Entry)>? EntryModified;
        public event EventHandler<string>? EntryRemoved;

        public StringTable(string tableId, string localeTag,
                          string displayName = "", string? description = null)
        {
            TableId = tableId ?? throw new ArgumentNullException(nameof(tableId));
            LocaleTag = localeTag ?? throw new ArgumentNullException(nameof(localeTag));
            DisplayName = displayName ?? tableId;
            Description = description;
        }

        // ── CRUD 操作 ──

        /// <summary>添加或更新一条字符串</summary>
        public void Set(string key, string value, string? comment = null)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("键不能为空", nameof(key));

            var entry = new StringTableEntry
            {
                Key = key,
                Value = value ?? "",
                Comment = comment
            };

            bool isNew = !_entries.ContainsKey(key);
            _entries[key] = entry;

            if (isNew)
                EntryAdded?.Invoke(this, entry);
            else
                EntryModified?.Invoke(this, (key, entry));
        }

        /// <summary>添加或更新一条完整条目</summary>
        public void Set(StringTableEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key))
                throw new ArgumentException("条目或键不能为空");

            bool isNew = !_entries.ContainsKey(entry.Key);
            _entries[entry.Key] = entry;

            if (isNew)
                EntryAdded?.Invoke(this, entry);
            else
                EntryModified?.Invoke(this, (entry.Key, entry));
        }

        /// <summary>获取一条字符串</summary>
        public string? Get(string key)
        {
            if (_entries.TryGetValue(key, out var entry))
                return entry.Value;
            return null;
        }

        /// <summary>尝试获取完整条目</summary>
        public bool TryGet(string key, out StringTableEntry? entry)
        {
            return _entries.TryGetValue(key, out entry);
        }

        /// <summary>是否存在某键</summary>
        public bool Contains(string key) => _entries.ContainsKey(key);

        /// <summary>移除一条字符串</summary>
        public bool Remove(string key)
        {
            if (_entries.Remove(key))
            {
                EntryRemoved?.Invoke(this, key);
                return true;
            }
            return false;
        }

        /// <summary>清空所有条目</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        // ── 搜索 ──

        /// <summary>按键搜索（支持通配符 * 和 ?）</summary>
        public IEnumerable<StringTableEntry> SearchByKey(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return _entries.Values;

            var regex = WildcardToRegex(pattern);
            return _entries.Values.Where(e => regex.IsMatch(e.Key));
        }

        /// <summary>按值搜索</summary>
        public IEnumerable<StringTableEntry> SearchByValue(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return _entries.Values;

            var regex = WildcardToRegex(pattern);
            return _entries.Values.Where(e => regex.IsMatch(e.Value));
        }

        /// <summary>按状态筛选</summary>
        public IEnumerable<StringTableEntry> FilterByState(StringEntryState state)
        {
            return _entries.Values.Where(e => e.State == state);
        }

        /// <summary>获取未翻译的条目</summary>
        public IEnumerable<StringTableEntry> GetUntranslated()
        {
            return _entries.Values.Where(e =>
                e.State == StringEntryState.Untranslated ||
                string.IsNullOrEmpty(e.Value));
        }

        /// <summary>获取所有条目的只读字典视图</summary>
        public IReadOnlyDictionary<string, StringTableEntry> GetAllEntries() => _entries;

        private static Regex WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StringTableCollection —— 多语言字符串集合
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 字符串表集合。管理同一组 Key 在不同语言下的翻译。
    /// 类似 UE 的 String Table Collection / Unity 的 String Table Collection。
    /// 一个 Collection 包含 TableID → 各语言 StringTable 的映射。
    /// </summary>
    public sealed class StringTableCollection
    {
        /// <summary>集合唯一标识</summary>
        public string CollectionId { get; }

        /// <summary>集合显示名称</summary>
        public string DisplayName { get; set; }

        /// <summary>集合描述</summary>
        public string? Description { get; set; }

        /// <summary>源语言（Key 的原始语言）</summary>
        public string SourceLocaleTag { get; set; } = "en-US";

        /// <summary>集合中已加载的语言标签</summary>
        public IEnumerable<string> LoadedLocaleTags => _tables.Keys;

        /// <summary>已加载语言数量</summary>
        public int LoadedLocaleCount => _tables.Count;

        /// <summary>基础语言表的 Key 集合（用于覆盖率计算）</summary>
        public IEnumerable<string> BaseKeys =>
            _tables.TryGetValue(SourceLocaleTag, out var baseTable)
                ? baseTable.Keys
                : Array.Empty<string>();

        /// <summary>所有 Key（取所有语言的并集）</summary>
        public IEnumerable<string> AllKeys
        {
            get
            {
                var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in _tables.Values)
                {
                    foreach (var key in table.Keys)
                        keySet.Add(key);
                }
                return keySet;
            }
        }

        /// <summary>字符串表加载/变更事件</summary>
        public event EventHandler<(string LocaleTag, StringTable Table)>? TableLoaded;
        public event EventHandler<string>? TableUnloaded;
        public event EventHandler? CollectionModified;

        /// <summary>语言标签 → StringTable 映射</summary>
        private readonly Dictionary<string, StringTable> _tables =
            new(StringComparer.OrdinalIgnoreCase);

        public StringTableCollection(string collectionId,
                                     string displayName = "",
                                     string? description = null)
        {
            CollectionId = collectionId ?? throw new ArgumentNullException(nameof(collectionId));
            DisplayName = displayName ?? collectionId;
            Description = description;
        }

        // ── 字符串表管理 ──

        /// <summary>添加一个语言的字符串表到集合</summary>
        public void AddTable(StringTable table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            _tables[table.LocaleTag] = table;
            TableLoaded?.Invoke(this, (table.LocaleTag, table));
            CollectionModified?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>获取指定语言的字符串表</summary>
        public StringTable? GetTable(string localeTag)
        {
            _tables.TryGetValue(localeTag, out var table);
            return table;
        }

        /// <summary>移除一个语言的字符串表</summary>
        public bool RemoveTable(string localeTag)
        {
            if (_tables.Remove(localeTag))
            {
                TableUnloaded?.Invoke(this, localeTag);
                CollectionModified?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }

        /// <summary>是否存在某语言的字符串表</summary>
        public bool HasTable(string localeTag) => _tables.ContainsKey(localeTag);

        /// <summary>获取所有字符串表</summary>
        public IEnumerable<StringTable> GetAllTables() => _tables.Values;

        // ── 字符串查找 ──

        /// <summary>
        /// 查找本地化字符串。
        /// 查找顺序：目标语言表 → 源语言表 → 返回 Key。
        /// </summary>
        public string GetLocalizedString(string localeTag, string key)
        {
            // 1. 目标语言
            if (_tables.TryGetValue(localeTag, out var table) && table.Contains(key))
                return table.Get(key) ?? key;

            // 2. 源语言（回退）
            if (!string.Equals(localeTag, SourceLocaleTag, StringComparison.OrdinalIgnoreCase))
            {
                if (_tables.TryGetValue(SourceLocaleTag, out var sourceTable) && sourceTable.Contains(key))
                    return sourceTable.Get(key) ?? key;
            }

            // 3. 返回 Key
            return key;
        }

        /// <summary>带参数的本地化字符串查找</summary>
        public string GetLocalizedString(string localeTag, string key,
                                         params object[] args)
        {
            var text = GetLocalizedString(localeTag, key);
            if (args == null || args.Length == 0) return text;

            try
            {
                return string.Format(text, args);
            }
            catch
            {
                return text;
            }
        }

        /// <summary>带命名参数的本地化字符串查找</summary>
        public string GetLocalizedStringFormatted(string localeTag, string key,
                                                   Dictionary<string, object> namedArgs)
        {
            var text = GetLocalizedString(localeTag, key);
            if (namedArgs == null || namedArgs.Count == 0) return text;

            // 替换 {Name} 格式的命名参数
            foreach (var kvp in namedArgs)
            {
                text = text.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? "");
            }
            return text;
        }

        // ── 缺失翻译报告 ──

        /// <summary>
        /// 获取指定语言中缺失的 Key 列表（相对于源语言表）。
        /// </summary>
        public IEnumerable<string> GetMissingKeys(string localeTag)
        {
            if (!_tables.TryGetValue(localeTag, out var targetTable))
                return BaseKeys; // 语言表不存在，全部缺失

            return BaseKeys.Where(key => !targetTable.Contains(key));
        }

        /// <summary>
        /// 获取指定语言中有但源语言没有的多余 Key。
        /// </summary>
        public IEnumerable<string> GetExtraKeys(string localeTag)
        {
            if (!_tables.TryGetValue(localeTag, out var targetTable))
                return Array.Empty<string>();

            var baseKeys = new HashSet<string>(BaseKeys, StringComparer.OrdinalIgnoreCase);
            return targetTable.Keys.Where(key => !baseKeys.Contains(key));
        }

        /// <summary>
        /// 获取翻译覆盖率（相对于源语言表）。
        /// </summary>
        public CoverageReport GetCoverageReport(string localeTag)
        {
            var baseKeysList = BaseKeys.ToList();
            int totalKeys = baseKeysList.Count;

            if (totalKeys == 0)
                return new CoverageReport(localeTag, 0, 0, 0, 0, 1.0f);

            var missingKeys = GetMissingKeys(localeTag).ToList();
            int translatedCount = totalKeys - missingKeys.Count;
            float coverage = (float)translatedCount / totalKeys;

            int criticalMissing = 0;
            if (_tables.TryGetValue(SourceLocaleTag, out var sourceTable))
            {
                foreach (var key in missingKeys)
                {
                    if (sourceTable.TryGet(key, out var entry) && entry.IsCritical)
                        criticalMissing++;
                }
            }

            // 按状态统计
            int draftCount = 0, reviewedCount = 0, approvedCount = 0;
            if (_tables.TryGetValue(localeTag, out var targetTable))
            {
                foreach (var entry in targetTable.Entries)
                {
                    switch (entry.State)
                    {
                        case StringEntryState.Draft: draftCount++; break;
                        case StringEntryState.Reviewed: reviewedCount++; break;
                        case StringEntryState.Approved: approvedCount++; break;
                    }
                }
            }

            return new CoverageReport(
                localeTag, totalKeys, translatedCount,
                missingKeys.Count, criticalMissing, coverage)
            {
                DraftCount = draftCount,
                ReviewedCount = reviewedCount,
                ApprovedCount = approvedCount
            };
        }

        /// <summary>获取所有语言的覆盖率报告</summary>
        public IEnumerable<CoverageReport> GetAllCoverageReports()
        {
            return _tables.Keys.Select(tag => GetCoverageReport(tag));
        }

        // ── 批量导入导出 ──

        /// <summary>从 CSV 批量导入字符串到指定语言表</summary>
        public int ImportFromCsv(string localeTag, string csvContent,
                                 bool overwrite = true)
        {
            var table = EnsureTable(localeTag);
            var lines = csvContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int imported = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                if (i == 0 && line.StartsWith("key,", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = ParseCsvLine(line);
                if (parts.Length < 2) continue;

                if (!overwrite && table.Contains(parts[0])) continue;

                table.Set(parts[0], parts[1]);
                imported++;
            }
            return imported;
        }

        /// <summary>从 JSON 批量导入字符串到指定语言表</summary>
        public int ImportFromJson(string localeTag, string jsonContent,
                                 bool overwrite = true)
        {
            var table = EnsureTable(localeTag);
            int imported = 0;

            using var doc = JsonDocument.Parse(jsonContent);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString() ?? "";
                    if (!overwrite && table.Contains(prop.Name)) continue;
                    table.Set(prop.Name, val);
                    imported++;
                }
            }
            return imported;
        }

        /// <summary>导出指定语言表为 CSV 格式</summary>
        public string ExportToCsv(string localeTag)
        {
            var table = GetTable(localeTag);
            if (table == null) return $"# No table found for {localeTag}\n";

            var sb = new StringBuilder();
            sb.AppendLine("key,value,state,comment");

            foreach (var entry in table.Entries.OrderBy(e => e.Key))
            {
                var escapedKey = EscapeCsvField(entry.Key);
                var escapedValue = EscapeCsvField(entry.Value);
                var state = entry.State.ToString();
                var comment = EscapeCsvField(entry.Comment ?? "");
                sb.AppendLine($"{escapedKey},{escapedValue},{state},{comment}");
            }
            return sb.ToString();
        }

        /// <summary>导出指定语言表为 JSON 格式</summary>
        public string ExportToJson(string localeTag, bool indent = true)
        {
            var table = GetTable(localeTag);
            if (table == null) return "{}";

            var options = new JsonSerializerOptions
            {
                WriteIndented = indent,
                PropertyNamingPolicy = null
            };

            var dict = new Dictionary<string, object>();
            foreach (var entry in table.Entries.OrderBy(e => e.Key))
            {
                dict[entry.Key] = entry.Value;
            }

            return JsonSerializer.Serialize(dict, options);
        }

        /// <summary>导出为多语言对照 CSV（每种语言一列）</summary>
        public string ExportMultilingualCsv()
        {
            var localeTags = _tables.Keys.OrderBy(t => t).ToList();
            var allKeys = AllKeys.OrderBy(k => k).ToList();

            var sb = new StringBuilder();
            sb.Append("key");
            foreach (var tag in localeTags)
                sb.Append($",{tag}");
            sb.AppendLine();

            foreach (var key in allKeys)
            {
                sb.Append(EscapeCsvField(key));
                foreach (var tag in localeTags)
                {
                    var table = _tables[tag];
                    var value = table.Get(key) ?? "";
                    sb.Append($",{EscapeCsvField(value)}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ── 内部工具 ──

        private StringTable EnsureTable(string localeTag)
        {
            if (!_tables.TryGetValue(localeTag, out var table))
            {
                table = new StringTable(CollectionId, localeTag,
                    $"{DisplayName} ({localeTag})");
                _tables[localeTag] = table;
                TableLoaded?.Invoke(this, (localeTag, table));
            }
            return table;
        }

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
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                    continue;
                }
                if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        private static string EscapeCsvField(string field)
        {
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // CoverageReport —— 覆盖率报告
    // ══════════════════════════════════════════════════════════════════

    /// <summary>翻译覆盖率报告</summary>
    public sealed class CoverageReport
    {
        /// <summary>语言标签</summary>
        public string LocaleTag { get; }

        /// <summary>总 Key 数（以源语言表为基准）</summary>
        public int TotalKeys { get; }

        /// <summary>已翻译 Key 数</summary>
        public int TranslatedCount { get; }

        /// <summary>缺失 Key 数</summary>
        public int MissingCount { get; }

        /// <summary>关键字符串缺失数</summary>
        public int CriticalMissingCount { get; }

        /// <summary>覆盖率（0.0 - 1.0）</summary>
        public float CoverageRatio { get; }

        /// <summary>草稿数量</summary>
        public int DraftCount { get; set; }

        /// <summary>已审核数量</summary>
        public int ReviewedCount { get; set; }

        /// <summary>已批准数量</summary>
        public int ApprovedCount { get; set; }

        /// <summary>覆盖率百分比</summary>
        public float CoveragePercent => CoverageRatio * 100f;

        public CoverageReport(string localeTag, int totalKeys, int translatedCount,
                              int missingCount, int criticalMissing, float coverageRatio)
        {
            LocaleTag = localeTag;
            TotalKeys = totalKeys;
            TranslatedCount = translatedCount;
            MissingCount = missingCount;
            CriticalMissingCount = criticalMissing;
            CoverageRatio = coverageRatio;
        }

        public override string ToString() =>
            $"[{LocaleTag}] {CoveragePercent:F1}% ({TranslatedCount}/{TotalKeys}), " +
            $"Missing: {MissingCount}, Critical: {CriticalMissingCount}";
    }

    // ══════════════════════════════════════════════════════════════════
    // StringTableRegistry —— 全局字符串表集合注册中心
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 字符串表注册中心。管理所有 StringTableCollection 的注册与查找。
    /// 提供 GetLocalizedString(tableId, localeTag, key) 的统一入口。
    /// </summary>
    public sealed class StringTableRegistry
    {
        private static readonly Lazy<StringTableRegistry> _lazy =
            new(() => new StringTableRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static StringTableRegistry Instance => _lazy.Value;

        /// <summary>CollectionId → StringTableCollection 映射</summary>
        private readonly Dictionary<string, StringTableCollection> _collections =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>字符串集合添加事件</summary>
        public event EventHandler<StringTableCollection>? CollectionRegistered;
        public event EventHandler<string>? CollectionRemoved;

        private StringTableRegistry() { }

        // ── 集合管理 ──

        /// <summary>注册一个字符串集合</summary>
        public void RegisterCollection(StringTableCollection collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            _collections[collection.CollectionId] = collection;
            CollectionRegistered?.Invoke(this, collection);
        }

        /// <summary>取消注册一个字符串集合</summary>
        public bool UnregisterCollection(string collectionId)
        {
            if (_collections.Remove(collectionId))
            {
                CollectionRemoved?.Invoke(this, collectionId);
                return true;
            }
            return false;
        }

        /// <summary>获取指定集合</summary>
        public StringTableCollection? GetCollection(string collectionId)
        {
            _collections.TryGetValue(collectionId, out var collection);
            return collection;
        }

        /// <summary>是否存在指定集合</summary>
        public bool HasCollection(string collectionId) => _collections.ContainsKey(collectionId);

        /// <summary>获取所有集合</summary>
        public IEnumerable<StringTableCollection> GetAllCollections() => _collections.Values;

        /// <summary>集合数量</summary>
        public int CollectionCount => _collections.Count;

        // ── 统一查找入口 ──

        /// <summary>
        /// 全局字符串查找入口。
        /// 在指定集合中查找指定语言的字符串。
        /// </summary>
        public string GetLocalizedString(string collectionId, string localeTag, string key)
        {
            if (!_collections.TryGetValue(collectionId, out var collection))
                return key;

            return collection.GetLocalizedString(localeTag, key);
        }

        /// <summary>带参数的全局字符串查找</summary>
        public string GetLocalizedString(string collectionId, string localeTag,
                                         string key, params object[] args)
        {
            var text = GetLocalizedString(collectionId, localeTag, key);
            if (args == null || args.Length == 0) return text;

            try { return string.Format(text, args); }
            catch { return text; }
        }

        // ── RESX 格式加载支持 ──

        /// <summary>
        /// 从 RESX 格式字符串加载字符串表。
        /// RESX 格式示例：
        ///   &lt;data name="UI.Start" xml:space="preserve"&gt;
        ///     &lt;value&gt;Start Game&lt;/value&gt;
        ///   &lt;/data&gt;
        /// </summary>
        public static StringTable FromResx(string resxContent, string tableId, string localeTag)
        {
            var table = new StringTable(tableId, localeTag);

            // 简化的 RESX XML 解析
            var dataPattern = new Regex(
                @"<data\s+name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>\s*</data>",
                RegexOptions.Singleline | RegexOptions.Compiled);

            foreach (Match match in dataPattern.Matches(resxContent))
            {
                var key = match.Groups[1].Value;
                var value = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                table.Set(key, value);
            }

            return table;
        }

        /// <summary>从 RESX 文件加载</summary>
        public static StringTable FromResxFile(string path, string tableId, string localeTag)
        {
            return FromResx(File.ReadAllText(path, Encoding.UTF8), tableId, localeTag);
        }

        // ── PO 格式加载支持 ──

        /// <summary>
        /// 从 GNU gettext PO 格式字符串加载字符串表。
        /// </summary>
        public static StringTable FromPo(string poContent, string tableId, string localeTag)
        {
            var table = new StringTable(tableId, localeTag);
            var lines = poContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

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
                if (line.StartsWith("#")) continue;

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

        /// <summary>从 PO 文件加载</summary>
        public static StringTable FromPoFile(string path, string tableId, string localeTag)
        {
            return FromPo(File.ReadAllText(path, Encoding.UTF8), tableId, localeTag);
        }

        private static string UnquotePo(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\n", "\n")
                     .Replace("\\t", "\t").Replace("\\\\", "\\");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 格式化参数扩展
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 高级字符串格式化器。支持位置参数、命名参数、复数形式、性别变体的组合。
    /// </summary>
    public static class StringFormatter
    {
        // 匹配 {Name} 命名参数
        private static readonly Regex NamedArgPattern =
            new(@"\{(\w+)\}", RegexOptions.Compiled);

        // 匹配 {0} 位置参数
        private static readonly Regex PositionalArgPattern =
            new(@"\{(\d+)\}", RegexOptions.Compiled);

        /// <summary>
        /// 格式化字符串。支持混合使用命名参数和位置参数。
        /// </summary>
        /// <param name="template">模板字符串</param>
        /// <param name="namedArgs">命名参数字典</param>
        /// <param name="positionalArgs">位置参数数组</param>
        public static string Format(string template,
                                   Dictionary<string, object>? namedArgs = null,
                                   object[]? positionalArgs = null)
        {
            if (string.IsNullOrEmpty(template)) return template;

            var result = template;

            // 替换命名参数 {Name}
            if (namedArgs != null && namedArgs.Count > 0)
            {
                result = NamedArgPattern.Replace(result, match =>
                {
                    var name = match.Groups[1].Value;
                    if (namedArgs.TryGetValue(name, out var value))
                        return value?.ToString() ?? "";
                    return match.Value; // 保留未匹配的占位符
                });
            }

            // 替换位置参数 {0}, {1}, ...
            if (positionalArgs != null && positionalArgs.Length > 0)
            {
                result = PositionalArgPattern.Replace(result, match =>
                {
                    if (int.TryParse(match.Groups[1].Value, out int index) &&
                        index >= 0 && index < positionalArgs.Length)
                    {
                        return positionalArgs[index]?.ToString() ?? "";
                    }
                    return match.Value;
                });
            }

            return result;
        }

        /// <summary>
        /// 带复数形式的格式化。
        /// 当模板中包含 {count} 或 {Count} 时，根据 count 值和语言选择复数形式。
        /// </summary>
        public static string FormatPlural(string template, int count,
                                           string localeTag,
                                           Dictionary<string, string>? pluralForms = null,
                                           Dictionary<string, object>? extraArgs = null)
        {
            // 如果有预定义的复数形式，直接选择
            if (pluralForms != null && pluralForms.Count > 0)
            {
                var formKey = GetPluralFormKey(count, localeTag);
                if (pluralForms.TryGetValue(formKey, out var pluralText))
                    template = pluralText;
            }

            // 构建 args 并格式化
            var args = new Dictionary<string, object>(extraArgs ?? new Dictionary<string, object>())
            {
                ["count"] = count,
                ["Count"] = count
            };
            return Format(template, args);
        }

        /// <summary>根据数量和语言获取复数形式 Key</summary>
        private static string GetPluralFormKey(int count, string localeTag)
        {
            var langCode = localeTag.Split('-')[0].ToLowerInvariant();
            return langCode switch
            {
                "zh" or "ja" or "ko" or "th" or "vi" => "other",
                "en" or "de" or "es" or "pt" or "it" or "nl" => count == 1 ? "one" : "other",
                "fr" => (count == 0 || count == 1) ? "one" : "other",
                "ru" or "uk" or "pl" => count switch
                {
                    1 => "one",
                    int c when c % 10 >= 2 && c % 10 <= 4 && (c % 100 < 10 || c % 100 >= 20) => "few",
                    _ => "many"
                },
                "ar" => count switch
                {
                    0 => "zero",
                    1 => "one",
                    2 => "two",
                    int c when c % 100 >= 3 && c % 100 <= 10 => "few",
                    int c when c % 100 >= 11 && c % 100 <= 99 => "many",
                    _ => "other"
                },
                _ => count == 1 ? "one" : "other"
            };
        }
    }
}
