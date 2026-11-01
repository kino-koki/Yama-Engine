using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YamaEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // 本地化资产系统（LocalizationAssetSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现本地化资源管理，覆盖文本/音频/图像/视频/字体等资产类型。
    //   1. LocalizationAsset 基类：Key → 资源路径映射
    //   2. LocalizedTextAsset：富文本/Markdown 本地化文本
    //   3. LocalizedAudioAsset：配音/旁白本地化音频
    //   4. LocalizedImageAsset：特定语言版本的图片/截图
    //   5. LocalizedFontAsset：每种语言指定字体 + 字号调整
    //   6. LocalizationAssetRegistry：全局资产注册中心
    //   7. 资产自动解析与回退机制
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化资产类型枚举。
    /// </summary>
    public enum LocalizationAssetType
    {
        /// <summary>文本资产</summary>
        Text,
        /// <summary>音频资产</summary>
        Audio,
        /// <summary>图像资产</summary>
        Image,
        /// <summary>视频资产</summary>
        Video,
        /// <summary>字体资产</summary>
        Font,
        /// <summary>自定义/其他资产</summary>
        Custom
    }

    /// <summary>
    /// 本地化资产回退策略。
    /// </summary>
    public enum AssetFallbackStrategy
    {
        /// <summary>首选语言缺失 → 回退语言 → 默认语言</summary>
        Cascade,
        /// <summary>仅尝试当前语言，缺失返回 null</summary>
        None,
        /// <summary>首选语言缺失 → 直接使用默认语言</summary>
        DefaultOnly
    }

    /// <summary>
    /// 本地化资产基类。定义 Key → 资源路径的映射，
    /// 支持按语言选择不同的资源版本。
    /// </summary>
    public abstract class LocalizationAsset
    {
        /// <summary>资产唯一标识</summary>
        public string AssetId { get; }

        /// <summary>资产类型</summary>
        public abstract LocalizationAssetType AssetType { get; }

        /// <summary>资产显示名称</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>资产描述</summary>
        public string? Description { get; set; }

        /// <summary>资产根目录（资源的基础路径）</summary>
        public string RootPath { get; set; } = "";

        /// <summary>回退策略</summary>
        public AssetFallbackStrategy FallbackStrategy { get; set; } = AssetFallbackStrategy.Cascade;

        protected LocalizationAsset(string assetId)
        {
            AssetId = assetId ?? throw new ArgumentNullException(nameof(assetId));
        }

        /// <summary>
        /// 获取指定语言下的资源路径。
        /// </summary>
        public abstract string? GetAssetPath(string localeTag);

        /// <summary>
        /// 检查指定语言是否存在资源。
        /// </summary>
        public abstract bool HasAssetForLocale(string localeTag);

        /// <summary>获取所有已注册资源的语言标签</summary>
        public abstract IEnumerable<string> GetRegisteredLocales();
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizedTextAsset —— 本地化文本资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化文本资产。支持富文本和 Markdown 格式的长文本内容，
    /// 如书籍、对话文本、教程说明等。
    /// </summary>
    public sealed class LocalizedTextAsset : LocalizationAsset
    {
        public override LocalizationAssetType AssetType => LocalizationAssetType.Text;

        /// <summary>是否支持富文本</summary>
        public bool SupportsRichText { get; set; }

        /// <summary>是否支持 Markdown</summary>
        public bool SupportsMarkdown { get; set; }

        /// <summary>文本编码（默认 UTF-8）</summary>
        public string Encoding { get; set; } = "UTF-8";

        /// <summary>语言标签 → 文件路径映射</summary>
        private readonly Dictionary<string, string> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>语言标签 → 缓存文本内容</summary>
        private readonly Dictionary<string, string> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否启用缓存</summary>
        public bool CacheEnabled { get; set; } = true;

        public LocalizedTextAsset(string assetId, bool richText = false,
                                  bool markdown = false)
            : base(assetId)
        {
            SupportsRichText = richText;
            SupportsMarkdown = markdown;
        }

        /// <summary>注册某语言的文本文件路径</summary>
        public void SetTextPath(string localeTag, string filePath)
        {
            if (!string.IsNullOrEmpty(localeTag) && !string.IsNullOrEmpty(filePath))
                _entries[localeTag] = filePath;
        }

        /// <summary>注册某语言的文本内容（内存）</summary>
        public void SetTextContent(string localeTag, string content)
        {
            if (!string.IsNullOrEmpty(localeTag))
            {
                _entries[localeTag] = $"__memory__:{localeTag}";
                _cache[localeTag] = content ?? "";
            }
        }

        public override string? GetAssetPath(string localeTag)
        {
            _entries.TryGetValue(localeTag, out var path);
            return path;
        }

        /// <summary>获取指定语言的文本内容</summary>
        public string? GetTextContent(string localeTag)
        {
            // 检查缓存
            if (CacheEnabled && _cache.TryGetValue(localeTag, out var cached))
                return cached;

            if (!_entries.TryGetValue(localeTag, out var path)) return null;

            if (path.StartsWith("__memory__:"))
            {
                return _cache.TryGetValue(localeTag, out var memContent) ? memContent : null;
            }

            // 从文件加载
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(RootPath, path);
            if (!File.Exists(fullPath)) return null;

            try
            {
                var content = File.ReadAllText(fullPath);
                if (CacheEnabled) _cache[localeTag] = content;
                return content;
            }
            catch
            {
                return null;
            }
        }

        public override bool HasAssetForLocale(string localeTag) => _entries.ContainsKey(localeTag);

        public override IEnumerable<string> GetRegisteredLocales() => _entries.Keys;

        /// <summary>清除缓存</summary>
        public void ClearCache() => _cache.Clear();
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizedAudioAsset —— 本地化音频资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化音频资产。管理配音、旁白、音效等按语言分版本的多媒体资源。
    /// </summary>
    public sealed class LocalizedAudioAsset : LocalizationAsset
    {
        public override LocalizationAssetType AssetType => LocalizationAssetType.Audio;

        /// <summary>音频格式（wav/mp3/ogg 等）</summary>
        public string Format { get; set; } = "wav";

        /// <summary>音频时长（秒）</summary>
        public float Duration { get; set; }

        /// <summary>采样率</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>语言标签 → 音频文件路径映射</summary>
        private readonly Dictionary<string, string> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public LocalizedAudioAsset(string assetId) : base(assetId) { }

        /// <summary>注册某语言的音频文件路径</summary>
        public void SetAudioPath(string localeTag, string filePath)
        {
            if (!string.IsNullOrEmpty(localeTag) && !string.IsNullOrEmpty(filePath))
                _entries[localeTag] = filePath;
        }

        public override string? GetAssetPath(string localeTag)
        {
            _entries.TryGetValue(localeTag, out var path);
            return path;
        }

        /// <summary>获取完整文件路径</summary>
        public string? GetFullPath(string localeTag)
        {
            if (!_entries.TryGetValue(localeTag, out var path)) return null;
            return Path.IsPathRooted(path) ? path : Path.Combine(RootPath, path);
        }

        /// <summary>检查文件是否存在</summary>
        public bool FileExists(string localeTag)
        {
            var fullPath = GetFullPath(localeTag);
            return fullPath != null && File.Exists(fullPath);
        }

        public override bool HasAssetForLocale(string localeTag) => _entries.ContainsKey(localeTag);

        public override IEnumerable<string> GetRegisteredLocales() => _entries.Keys;
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizedImageAsset —— 本地化图像资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化图像资产。管理按语言分版本的图片资源，
    /// 如本地化的 Logo、截图、教程图片等。
    /// </summary>
    public sealed class LocalizedImageAsset : LocalizationAsset
    {
        public override LocalizationAssetType AssetType => LocalizationAssetType.Image;

        /// <summary>图像宽度</summary>
        public int Width { get; set; }

        /// <summary>图像高度</summary>
        public int Height { get; set; }

        /// <summary>图像格式（png/jpg/webp 等）</summary>
        public string Format { get; set; } = "png";

        /// <summary>语言标签 → 图像文件路径映射</summary>
        private readonly Dictionary<string, string> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public LocalizedImageAsset(string assetId) : base(assetId) { }

        /// <summary>注册某语言的图像文件路径</summary>
        public void SetImagePath(string localeTag, string filePath)
        {
            if (!string.IsNullOrEmpty(localeTag) && !string.IsNullOrEmpty(filePath))
                _entries[localeTag] = filePath;
        }

        public override string? GetAssetPath(string localeTag)
        {
            _entries.TryGetValue(localeTag, out var path);
            return path;
        }

        /// <summary>获取完整文件路径</summary>
        public string? GetFullPath(string localeTag)
        {
            if (!_entries.TryGetValue(localeTag, out var path)) return null;
            return Path.IsPathRooted(path) ? path : Path.Combine(RootPath, path);
        }

        public override bool HasAssetForLocale(string localeTag) => _entries.ContainsKey(localeTag);

        public override IEnumerable<string> GetRegisteredLocales() => _entries.Keys;
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizedVideoAsset —— 本地化视频资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化视频资产。管理本地化的过场动画、教程视频等。
    /// </summary>
    public sealed class LocalizedVideoAsset : LocalizationAsset
    {
        public override LocalizationAssetType AssetType => LocalizationAssetType.Video;

        /// <summary>视频格式（mp4/webm 等）</summary>
        public string Format { get; set; } = "mp4";

        /// <summary>视频时长（秒）</summary>
        public float Duration { get; set; }

        /// <summary>视频分辨率宽度</summary>
        public int ResolutionWidth { get; set; } = 1920;

        /// <summary>视频分辨率高度</summary>
        public int ResolutionHeight { get; set; } = 1080;

        private readonly Dictionary<string, string> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public LocalizedVideoAsset(string assetId) : base(assetId) { }

        public void SetVideoPath(string localeTag, string filePath)
        {
            if (!string.IsNullOrEmpty(localeTag) && !string.IsNullOrEmpty(filePath))
                _entries[localeTag] = filePath;
        }

        public override string? GetAssetPath(string localeTag)
        {
            _entries.TryGetValue(localeTag, out var path);
            return path;
        }

        public string? GetFullPath(string localeTag)
        {
            if (!_entries.TryGetValue(localeTag, out var path)) return null;
            return Path.IsPathRooted(path) ? path : Path.Combine(RootPath, path);
        }

        public override bool HasAssetForLocale(string localeTag) => _entries.ContainsKey(localeTag);

        public override IEnumerable<string> GetRegisteredLocales() => _entries.Keys;
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizedFontAsset —— 本地化字体资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化字体资产。为每种语言指定字体文件和字号调整系数。
    /// 不同语言可能需要不同字体（如 CJK 语言需要中日韩字体），
    /// 即使相同字体也可能需要不同字号（如阿拉伯语通常需要放大）。
    /// </summary>
    public sealed class LocalizedFontAsset : LocalizationAsset
    {
        public override LocalizationAssetType AssetType => LocalizationAssetType.Font;

        /// <summary>字体配置：语言标签 → FontConfig</summary>
        private readonly Dictionary<string, FontConfig> _configs =
            new(StringComparer.OrdinalIgnoreCase);

        public LocalizedFontAsset(string assetId) : base(assetId) { }

        /// <summary>为指定语言设置字体配置</summary>
        public void SetFontConfig(string localeTag, FontConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _configs[localeTag] = config;
        }

        /// <summary>为指定语言设置字体（简化版）</summary>
        public void SetFont(string localeTag, string fontPath,
                           float sizeScale = 1.0f, string? fallbackFont = null)
        {
            _configs[localeTag] = new FontConfig
            {
                FontPath = fontPath,
                SizeScale = sizeScale,
                FallbackFont = fallbackFont
            };
        }

        /// <summary>获取指定语言的字体配置</summary>
        public FontConfig? GetFontConfig(string localeTag)
        {
            _configs.TryGetValue(localeTag, out var config);
            return config;
        }

        /// <summary>获取字体文件完整路径</summary>
        public string? GetFontPath(string localeTag)
        {
            if (_configs.TryGetValue(localeTag, out var config))
                return config.GetFullPath(RootPath);
            return null;
        }

        /// <summary>获取字号缩放系数</summary>
        public float GetSizeScale(string localeTag)
        {
            if (_configs.TryGetValue(localeTag, out var config))
                return config.SizeScale;
            return 1.0f;
        }

        /// <summary>根据缩放系数计算最终字号</summary>
        public float GetScaledSize(string localeTag, float baseSize)
        {
            return baseSize * GetSizeScale(localeTag);
        }

        public override string? GetAssetPath(string localeTag)
        {
            return GetFontPath(localeTag);
        }

        public override bool HasAssetForLocale(string localeTag) => _configs.ContainsKey(localeTag);

        public override IEnumerable<string> GetRegisteredLocales() => _configs.Keys;
    }

    /// <summary>
    /// 字体配置。描述某种语言使用的字体文件及其属性。
    /// </summary>
    public sealed class FontConfig
    {
        /// <summary>字体文件路径（相对或绝对）</summary>
        public string FontPath { get; set; } = "";

        /// <summary>字体族名称（如 "NotoSansSC"）</summary>
        public string FamilyName { get; set; } = "";

        /// <summary>字号缩放系数（1.0 = 不缩放）</summary>
        public float SizeScale { get; set; } = 1.0f;

        /// <summary>回退字体（当首选字体缺少字形时使用）</summary>
        public string? FallbackFont { get; set; }

        /// <summary>字体粗细（100-900）</summary>
        public int Weight { get; set; } = 400;

        /// <summary>字体样式（Normal/Bold/Italic）</summary>
        public string Style { get; set; } = "Normal";

        /// <summary>行高倍数</summary>
        public float LineHeightMultiplier { get; set; } = 1.2f;

        /// <summary>字符间距（像素）</summary>
        public float CharacterSpacing { get; set; } = 0f;

        /// <summary>单词间距（像素）</summary>
        public float WordSpacing { get; set; } = 0f;

        /// <summary>获取完整路径</summary>
        public string GetFullPath(string rootPath)
        {
            if (string.IsNullOrEmpty(FontPath)) return "";
            return Path.IsPathRooted(FontPath) ? FontPath : Path.Combine(rootPath, FontPath);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // LocalizationAssetRegistry —— 全局资产注册中心
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地化资产注册中心。管理所有类型本地化资产的注册与查找。
    /// 运行时根据当前语言自动选择正确的资产版本。
    /// </summary>
    public sealed class LocalizationAssetRegistry
    {
        private static readonly Lazy<LocalizationAssetRegistry> _lazy =
            new(() => new LocalizationAssetRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static LocalizationAssetRegistry Instance => _lazy.Value;

        /// <summary>资产注册表</summary>
        private readonly Dictionary<string, LocalizationAsset> _assets =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>默认回退语言标签</summary>
        public string DefaultFallbackLocale { get; set; } = "en-US";

        /// <summary>资产注册事件</summary>
        public event EventHandler<LocalizationAsset>? AssetRegistered;
        public event EventHandler<string>? AssetUnregistered;

        private LocalizationAssetRegistry() { }

        // ── 资产注册 ──

        /// <summary>注册一个本地化资产</summary>
        public void RegisterAsset(LocalizationAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            _assets[asset.AssetId] = asset;
            AssetRegistered?.Invoke(this, asset);
        }

        /// <summary>取消注册一个本地化资产</summary>
        public bool UnregisterAsset(string assetId)
        {
            if (_assets.Remove(assetId))
            {
                AssetUnregistered?.Invoke(this, assetId);
                return true;
            }
            return false;
        }

        /// <summary>获取指定资产</summary>
        public LocalizationAsset? GetAsset(string assetId)
        {
            _assets.TryGetValue(assetId, out var asset);
            return asset;
        }

        /// <summary>是否存在指定资产</summary>
        public bool HasAsset(string assetId) => _assets.ContainsKey(assetId);

        /// <summary>获取所有资产</summary>
        public IEnumerable<LocalizationAsset> GetAllAssets() => _assets.Values;

        /// <summary>按类型获取资产</summary>
        public IEnumerable<LocalizationAsset> GetAssetsByType(LocalizationAssetType type)
        {
            return _assets.Values.Where(a => a.AssetType == type);
        }

        /// <summary>资产数量</summary>
        public int AssetCount => _assets.Count;

        // ── 运行时资产解析 ──

        /// <summary>
        /// 自动解析指定资产的正确资源路径。
        /// 查找顺序：当前语言 → 回退语言 → 默认语言。
        /// </summary>
        public string? ResolveAssetPath(string assetId, string currentLocale,
                                        string? fallbackLocale = null)
        {
            var asset = GetAsset(assetId);
            if (asset == null) return null;

            // 1. 当前语言
            var path = asset.GetAssetPath(currentLocale);
            if (path != null) return path;

            // 2. 回退语言
            if (fallbackLocale != null)
            {
                path = asset.GetAssetPath(fallbackLocale);
                if (path != null) return path;
            }

            // 3. 默认回退语言
            if (!string.Equals(currentLocale, DefaultFallbackLocale, StringComparison.OrdinalIgnoreCase))
            {
                path = asset.GetAssetPath(DefaultFallbackLocale);
                if (path != null) return path;
            }

            return null;
        }

        /// <summary>
        /// 自动解析文本资产内容。
        /// </summary>
        public string? ResolveTextContent(string assetId, string currentLocale,
                                           string? fallbackLocale = null)
        {
            var asset = GetAsset(assetId);
            if (asset is not LocalizedTextAsset textAsset) return null;

            // 1. 当前语言
            var content = textAsset.GetTextContent(currentLocale);
            if (content != null) return content;

            // 2. 回退语言
            if (fallbackLocale != null)
            {
                content = textAsset.GetTextContent(fallbackLocale);
                if (content != null) return content;
            }

            // 3. 默认回退
            if (!string.Equals(currentLocale, DefaultFallbackLocale, StringComparison.OrdinalIgnoreCase))
            {
                return textAsset.GetTextContent(DefaultFallbackLocale);
            }

            return null;
        }

        /// <summary>
        /// 自动解析字体配置。
        /// </summary>
        public FontConfig? ResolveFontConfig(string assetId, string currentLocale,
                                             string? fallbackLocale = null)
        {
            var asset = GetAsset(assetId);
            if (asset is not LocalizedFontAsset fontAsset) return null;

            var config = fontAsset.GetFontConfig(currentLocale);
            if (config != null) return config;

            if (fallbackLocale != null)
            {
                config = fontAsset.GetFontConfig(fallbackLocale);
                if (config != null) return config;
            }

            if (!string.Equals(currentLocale, DefaultFallbackLocale, StringComparison.OrdinalIgnoreCase))
            {
                return fontAsset.GetFontConfig(DefaultFallbackLocale);
            }

            return null;
        }

        // ── 便捷工厂方法 ──

        /// <summary>创建并注册文本资产</summary>
        public LocalizedTextAsset CreateTextAsset(string assetId,
                                                  bool richText = false,
                                                  bool markdown = false)
        {
            var asset = new LocalizedTextAsset(assetId, richText, markdown);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>创建并注册音频资产</summary>
        public LocalizedAudioAsset CreateAudioAsset(string assetId)
        {
            var asset = new LocalizedAudioAsset(assetId);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>创建并注册图像资产</summary>
        public LocalizedImageAsset CreateImageAsset(string assetId)
        {
            var asset = new LocalizedImageAsset(assetId);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>创建并注册视频资产</summary>
        public LocalizedVideoAsset CreateVideoAsset(string assetId)
        {
            var asset = new LocalizedVideoAsset(assetId);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>创建并注册字体资产</summary>
        public LocalizedFontAsset CreateFontAsset(string assetId)
        {
            var asset = new LocalizedFontAsset(assetId);
            RegisterAsset(asset);
            return asset;
        }
    }
}
