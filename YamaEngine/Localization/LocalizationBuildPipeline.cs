using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 本地化构建管线（LocalizationBuildPipeline）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现本地化相关的构建管线，与引擎主构建流程集成。
    //   1. 构建时收集所有字符串
    //   2. 合并字符串表（去重/冲突检测）
    //   3. 生成各语言资源包
    //   4. 热更新差异生成
    //   5. 构建验证（覆盖率/关键字符串）
    //   6. 字体子集化
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建验证结果。
    /// </summary>
    public enum LocalizationBuildResult
    {
        /// <summary>构建成功</summary>
        Success,
        /// <summary>构建成功但有警告</summary>
        SuccessWithWarnings,
        /// <summary>构建失败</summary>
        Failed
    }

    /// <summary>
    /// 本地化构建配置。
    /// </summary>
    public sealed class LocalizationBuildConfig
    {
        /// <summary>目标语言标签列表（null = 构建所有已注册语言）</summary>
        public List<string>? TargetLocales { get; set; }

        /// <summary>源语言标签</summary>
        public string SourceLocale { get; set; } = "en-US";

        /// <summary>最低覆盖率阈值（0.0 - 1.0），低于此值则构建警告</summary>
        public float MinimumCoverageThreshold { get; set; } = 0.8f;

        /// <summary>关键字符串必须全部翻译，否则构建失败</summary>
        public bool RequireCriticalStrings { get; set; } = true;

        /// <summary>是否生成热更新差异包</summary>
        public bool GenerateHotUpdatePatches { get; set; } = false;

        /// <summary>是否执行字体子集化</summary>
        public bool EnableFontSubsetting { get; set; } = false;

        /// <summary>输出目录</summary>
        public string OutputDirectory { get; set; } = "Localization";

        /// <summary>是否生成多语言对照 CSV</summary>
        public bool GenerateMultilingualCsv { get; set; } = true;

        /// <summary>是否生成统计报告</summary>
        public bool GenerateReport { get; set; } = true;

        /// <summary>是否压缩资源包</summary>
        public bool CompressPackages { get; set; } = true;
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizationBuildPipeline —— 本地化构建管线
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化构建管线。在项目构建时执行本地化相关的处理步骤。
    /// 集成到引擎主 BuildPipeline 中，作为构建流程的一部分。
    /// </summary>
    public sealed class LocalizationBuildPipeline
    {
        /// <summary>构建进度事件</summary>
        public event EventHandler<string>? BuildProgress;

        /// <summary>构建完成事件</summary>
        public event EventHandler<LocalizationBuildReport>? BuildCompleted;

        /// <summary>当前构建报告</summary>
        private LocalizationBuildReport? _currentReport;

        public LocalizationBuildReport? CurrentReport => _currentReport;

        /// <summary>
        /// 执行本地化构建。
        /// 完整流程：收集 → 验证 → 合并 → 生成 → 打包。
        /// </summary>
        public LocalizationBuildResult Build(LocalizationBuildConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _currentReport = new LocalizationBuildReport
            {
                Config = config,
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 1. 收集所有字符串
                BuildProgress?.Invoke(this, "收集字符串...");
                var allCollections = StringTableRegistry.Instance.GetAllCollections().ToList();
                _currentReport.TotalCollections = allCollections.Count;

                // 2. 验证
                BuildProgress?.Invoke(this, "验证字符串表...");
                Validate(config, allCollections);

                // 3. 合并字符串表（去重）
                BuildProgress?.Invoke(this, "合并字符串表...");
                var mergedCollection = MergeStringTables(allCollections);
                _currentReport.TotalUniqueKeys = mergedCollection.AllKeys.Count();

                // 4. 确定目标语言
                var targetLocales = GetTargetLocales(config, allCollections);

                // 5. 生成各语言资源包
                BuildProgress?.Invoke(this, "生成语言资源包...");
                foreach (var localeTag in targetLocales)
                {
                    GenerateLanguagePackage(mergedCollection, localeTag, config);
                }

                _currentReport.GeneratedLanguages = targetLocales.Count;

                // 6. 生成多语言对照 CSV
                if (config.GenerateMultilingualCsv)
                {
                    BuildProgress?.Invoke(this, "生成多语言对照表...");
                    GenerateMultilingualExport(mergedCollection, config);
                }

                // 7. 热更新差异
                if (config.GenerateHotUpdatePatches)
                {
                    BuildProgress?.Invoke(this, "生成热更新差异...");
                    GenerateHotUpdatePatches(mergedCollection, targetLocales, config);
                }

                // 8. 字体子集化
                if (config.EnableFontSubsetting)
                {
                    BuildProgress?.Invoke(this, "字体子集化...");
                    GenerateFontSubsets(mergedCollection, targetLocales, config);
                }

                // 9. 生成统计报告
                if (config.GenerateReport)
                {
                    BuildProgress?.Invoke(this, "生成统计报告...");
                    GenerateBuildReport(mergedCollection, targetLocales, config);
                }

                _currentReport.EndTime = DateTime.UtcNow;
                _currentReport.Duration = (_currentReport.EndTime - _currentReport.StartTime).TotalMilliseconds;

                var hasErrors = _currentReport.Errors.Count > 0;
                var hasWarnings = _currentReport.Warnings.Count > 0;

                _currentReport.Result = hasErrors
                    ? LocalizationBuildResult.Failed
                    : hasWarnings
                        ? LocalizationBuildResult.SuccessWithWarnings
                        : LocalizationBuildResult.Success;

                BuildCompleted?.Invoke(this, _currentReport);
                return _currentReport.Result;
            }
            catch (Exception ex)
            {
                _currentReport?.Errors.Add($"构建异常: {ex.Message}");
                _currentReport!.Result = LocalizationBuildResult.Failed;
                BuildCompleted?.Invoke(this, _currentReport);
                return LocalizationBuildResult.Failed;
            }
        }

        // ── 验证 ──

        /// <summary>验证字符串表完整性</summary>
        private void Validate(LocalizationBuildConfig config,
                              List<StringTableCollection> collections)
        {
            foreach (var collection in collections)
            {
                ValidateCollection(collection, config);
            }
        }

        /// <summary>验证单个字符串集合</summary>
        private void ValidateCollection(StringTableCollection collection,
                                         LocalizationBuildConfig config)
        {
            // 覆盖率检查
            foreach (var localeTag in collection.LoadedLocaleTags)
            {
                var report = collection.GetCoverageReport(localeTag);

                if (report.CoverageRatio < config.MinimumCoverageThreshold)
                {
                    _currentReport!.Warnings.Add(
                        $"[{collection.CollectionId}/{localeTag}] " +
                        $"覆盖率 {report.CoveragePercent:F1}% 低于阈值 " +
                        $"{config.MinimumCoverageThreshold * 100f:F0}%");
                }

                // 关键字符串检查
                if (config.RequireCriticalStrings && report.CriticalMissingCount > 0)
                {
                    _currentReport!.Errors.Add(
                        $"[{collection.CollectionId}/{localeTag}] " +
                        $"缺失 {report.CriticalMissingCount} 个关键字符串翻译");
                }
            }
        }

        // ── 合并字符串表 ──

        /// <summary>合并所有字符串集合为一个统一的合并集合</summary>
        private StringTableCollection MergeStringTables(
            List<StringTableCollection> collections)
        {
            var merged = new StringTableCollection(
                "__merged__", "Merged Strings",
                "All string collections merged for build");

            merged.SourceLocaleTag = collections.Count > 0
                ? collections[0].SourceLocaleTag
                : "en-US";

            // 收集所有语言标签
            var allLocaleTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var coll in collections)
            {
                foreach (var tag in coll.LoadedLocaleTags)
                    allLocaleTags.Add(tag);
            }

            // 逐语言合并
            foreach (var localeTag in allLocaleTags)
            {
                var mergedTable = new StringTable("__merged__", localeTag, $"Merged ({localeTag})");
                var keyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var coll in collections)
                {
                    var table = coll.GetTable(localeTag);
                    if (table == null) continue;

                    foreach (var entry in table.Entries)
                    {
                        // 去重：如果 Key 已存在，记录冲突
                        if (keyMap.ContainsKey(entry.Key))
                        {
                            var existingValue = mergedTable.Get(entry.Key);
                            if (existingValue != entry.Value)
                            {
                                _currentReport!.Warnings.Add(
                                    $"Key 冲突 '{entry.Key}' 在 {localeTag}: " +
                                    $"使用 '{existingValue}'（来自 {keyMap[entry.Key]}），" +
                                    $"忽略 '{entry.Value}'（来自 {coll.CollectionId}）");
                            }
                        }
                        else
                        {
                            mergedTable.Set(entry.Key, entry.Value);
                            keyMap[entry.Key] = coll.CollectionId;
                        }
                    }
                }

                merged.AddTable(mergedTable);
            }

            return merged;
        }

        // ── 确定目标语言 ──

        /// <summary>确定要构建的目标语言列表</summary>
        private List<string> GetTargetLocales(LocalizationBuildConfig config,
                                               List<StringTableCollection> collections)
        {
            if (config.TargetLocales != null && config.TargetLocales.Count > 0)
                return config.TargetLocales;

            var allLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var coll in collections)
            {
                foreach (var tag in coll.LoadedLocaleTags)
                    allLocales.Add(tag);
            }
            return allLocales.OrderBy(t => t).ToList();
        }

        // ── 生成语言资源包 ──

        /// <summary>为指定语言生成资源包</summary>
        private void GenerateLanguagePackage(StringTableCollection collection,
                                             string localeTag,
                                             LocalizationBuildConfig config)
        {
            var table = collection.GetTable(localeTag);
            if (table == null) return;

            var langDir = Path.Combine(config.OutputDirectory, localeTag);
            Directory.CreateDirectory(langDir);

            // 生成 JSON 字符串包
            var jsonContent = collection.ExportToJson(localeTag);
            var jsonPath = Path.Combine(langDir, "strings.json");
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);
            _currentReport!.GeneratedFiles.Add(jsonPath);

            // 生成 CSV 字符串包
            var csvContent = collection.ExportToCsv(localeTag);
            var csvPath = Path.Combine(langDir, "strings.csv");
            File.WriteAllText(csvPath, csvContent, Encoding.UTF8);
            _currentReport!.GeneratedFiles.Add(csvPath);

            // 生成语言清单
            var manifest = new
            {
                locale = localeTag,
                entryCount = table.Count,
                generatedAt = DateTime.UtcNow.ToString("O"),
                collections = new[] { collection.CollectionId }
            };
            var manifestPath = Path.Combine(langDir, "manifest.json");
            var manifestJson = JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);
            _currentReport!.GeneratedFiles.Add(manifestPath);
        }

        // ── 多语言对照导出 ──

        /// <summary>生成多语言对照表</summary>
        private void GenerateMultilingualExport(StringTableCollection collection,
                                                 LocalizationBuildConfig config)
        {
            var multilingualCsv = collection.ExportMultilingualCsv();
            var csvPath = Path.Combine(config.OutputDirectory, "multilingual.csv");
            File.WriteAllText(csvPath, multilingualCsv, Encoding.UTF8);
            _currentReport!.GeneratedFiles.Add(csvPath);
        }

        // ── 热更新差异 ──

        /// <summary>生成热更新差异包</summary>
        private void GenerateHotUpdatePatches(StringTableCollection collection,
                                              List<string> targetLocales,
                                              LocalizationBuildConfig config)
        {
            var patchesDir = Path.Combine(config.OutputDirectory, "patches");
            Directory.CreateDirectory(patchesDir);

            foreach (var localeTag in targetLocales)
            {
                var report = collection.GetCoverageReport(localeTag);
                var missingKeys = collection.GetMissingKeys(localeTag).ToList();

                if (missingKeys.Count > 0)
                {
                    var patch = new
                    {
                        locale = localeTag,
                        patchType = "missing_translations",
                        missingKeys = missingKeys,
                        totalMissing = missingKeys.Count,
                        criticalMissing = report.CriticalMissingCount,
                        generatedAt = DateTime.UtcNow.ToString("O")
                    };

                    var patchPath = Path.Combine(patchesDir, $"patch_{localeTag}.json");
                    var patchJson = JsonSerializer.Serialize(patch,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(patchPath, patchJson, Encoding.UTF8);
                    _currentReport!.GeneratedFiles.Add(patchPath);
                }
            }
        }

        // ── 字体子集化 ──

        /// <summary>为每种语言生成字体子集描述</summary>
        private void GenerateFontSubsets(StringTableCollection collection,
                                          List<string> targetLocales,
                                          LocalizationBuildConfig config)
        {
            var fontDir = Path.Combine(config.OutputDirectory, "fonts");
            Directory.CreateDirectory(fontDir);

            foreach (var localeTag in targetLocales)
            {
                var table = collection.GetTable(localeTag);
                if (table == null) continue;

                // 收集该语言需要的所有字符
                var charSet = new HashSet<char>();
                foreach (var entry in table.Entries)
                {
                    foreach (char c in entry.Value)
                        charSet.Add(c);
                }

                var subsetInfo = new
                {
                    locale = localeTag,
                    characterCount = charSet.Count,
                    characters = string.Concat(charSet.OrderBy(c => c)),
                    sampleCharacters = string.Concat(charSet.Take(100)),
                    note = "Font subsetting description - actual subsetting to be performed by font tools"
                };

                var subsetPath = Path.Combine(fontDir, $"subset_{localeTag}.json");
                var subsetJson = JsonSerializer.Serialize(subsetInfo,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(subsetPath, subsetJson, Encoding.UTF8);
                _currentReport!.GeneratedFiles.Add(subsetPath);
            }
        }

        // ── 构建报告 ──

        /// <summary>生成构建统计报告</summary>
        private void GenerateBuildReport(StringTableCollection collection,
                                         List<string> targetLocales,
                                         LocalizationBuildConfig config)
        {
            var report = new StringBuilder();
            report.AppendLine("=== yama引擎本地化构建报告 ===");
            report.AppendLine($"构建时间: {DateTime.UtcNow:O}");
            report.AppendLine($"源语言: {config.SourceLocale}");
            report.AppendLine($"目标语言数: {targetLocales.Count}");
            report.AppendLine();

            report.AppendLine("── 覆盖率统计 ──");
            foreach (var localeTag in targetLocales)
            {
                var coverage = collection.GetCoverageReport(localeTag);
                report.AppendLine(coverage.ToString());
            }

            report.AppendLine();
            report.AppendLine($"── 总唯一 Key 数: {collection.AllKeys.Count()} ──");
            report.AppendLine($"── 总生成文件数: {_currentReport!.GeneratedFiles.Count} ──");

            if (_currentReport.Warnings.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("── 警告 ──");
                foreach (var warning in _currentReport.Warnings)
                    report.AppendLine($"  [WARNING] {warning}");
            }

            if (_currentReport.Errors.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("── 错误 ──");
                foreach (var error in _currentReport.Errors)
                    report.AppendLine($"  [ERROR] {error}");
            }

            var reportPath = Path.Combine(config.OutputDirectory, "build_report.txt");
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            _currentReport!.GeneratedFiles.Add(reportPath);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizationBuildReport —— 构建报告
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化构建报告。记录构建过程中的所有信息、警告和错误。
    /// </summary>
    public sealed class LocalizationBuildReport
    {
        /// <summary>构建配置</summary>
        public LocalizationBuildConfig Config { get; set; } = new();

        /// <summary>构建结果</summary>
        public LocalizationBuildResult Result { get; set; }

        /// <summary>构建开始时间</summary>
        public DateTime StartTime { get; set; }

        /// <summary>构建结束时间</summary>
        public DateTime EndTime { get; set; }

        /// <summary>构建耗时（毫秒）</summary>
        public double Duration { get; set; }

        /// <summary>处理的总集合数</summary>
        public int TotalCollections { get; set; }

        /// <summary>合并后的唯一 Key 数</summary>
        public int TotalUniqueKeys { get; set; }

        /// <summary>生成的语言数</summary>
        public int GeneratedLanguages { get; set; }

        /// <summary>生成的文件列表</summary>
        public List<string> GeneratedFiles { get; set; } = new();

        /// <summary>警告列表</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>错误列表</summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>是否有错误</summary>
        public bool HasErrors => Errors.Count > 0;

        /// <summary>是否有警告</summary>
        public bool HasWarnings => Warnings.Count > 0;

        public override string ToString()
        {
            var status = Result.ToString();
            return $"Localization Build: {status}, " +
                   $"Collections: {TotalCollections}, " +
                   $"Keys: {TotalUniqueKeys}, " +
                   $"Languages: {GeneratedLanguages}, " +
                   $"Files: {GeneratedFiles.Count}, " +
                   $"Warnings: {Warnings.Count}, " +
                   $"Errors: {Errors.Count}, " +
                   $"Duration: {Duration:F0}ms";
        }
    }
}
