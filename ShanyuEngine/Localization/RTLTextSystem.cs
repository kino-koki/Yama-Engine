using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Localization
{
    // ══════════════════════════════════════════════════════════════════
    // RTL/双向文本系统（RTLTextSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现从右到左（RTL）文本和双向文本的完整支持。
    //   1. 文本方向检测（LTR/RTL/自动检测）
    //   2. UI 布局镜像（RTL 模式下坐标翻转）
    //   3. 数字方向处理（阿拉伯/印度-阿拉伯/波斯数字）
    //   4. 段落方向控制
    //   5. Unicode 双向算法简化实现
    //   6. UI Widget 对齐自动调整
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 段落方向枚举。控制整个段落/文本块的方向。
    /// </summary>
    public enum ParagraphDirection
    {
        /// <summary>从左到右</summary>
        LeftToRight,
        /// <summary>从右到左</summary>
        RightToLeft,
        /// <summary>根据首字符强方向性自动检测</summary>
        Auto
    }

    /// <summary>
    /// 数字系统枚举。
    /// </summary>
    public enum NumberSystem
    {
        /// <summary>阿拉伯数字 (0-9) —— 西方</summary>
        WesternArabic,
        /// <summary>印度-阿拉伯数字 —— 阿拉伯语</summary>
        EasternArabic,
        /// <summary>波斯/印度数字 —— 波斯语/印地语</summary>
        Persian,
        /// <summary>天城文数字 —— 印地语</summary>
        Devanagari,
        /// <summary>泰文数字</summary>
        Thai,
        /// <summary>中日韩数字</summary>
        CJK
    }

    /// <summary>
    /// Unicode 双向类型（简化版）。
    /// </summary>
    public enum BidiCharacterType
    {
        /// <summary>强左到右（如拉丁字母）</summary>
        LeftToRight,
        /// <summary>强右到左（如阿拉伯/希伯来字母）</summary>
        RightToLeft,
        /// <summary>欧洲数字分隔符</summary>
        EuropeanNumberSeparator,
        /// <summary>欧洲数字终止符</summary>
        EuropeanNumberTerminator,
        /// <summary>阿拉伯数字</summary>
        ArabicNumber,
        /// <summary>通用分隔符</summary>
        CommonSeparator,
        /// <summary>段落分隔符</summary>
        ParagraphSeparator,
        /// <summary>中性/其他</summary>
        Neutral,
        /// <summary>空白</summary>
        WhiteSpace
    }

    // ══════════════════════════════════════════════════════════════════
    // BidiCharacterClassifier —— Unicode 双向字符分类器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Unicode 双向字符分类器。根据字符的 Unicode 码位判断其双向类型。
    /// 简化实现，覆盖最常用的 RTL 字符范围。
    /// </summary>
    public static class BidiCharacterClassifier
    {
        /// <summary>判断字符的双向类型</summary>
        public static BidiCharacterType Classify(char c)
        {
            // 强 RTL 字符
            if (IsArabicCharacter(c) || IsHebrewCharacter(c))
                return BidiCharacterType.RightToLeft;

            // 强 LTR 字符
            if (IsLatinCharacter(c) || IsCJKCharacter(c) || IsCyrillicCharacter(c) ||
                IsGreekCharacter(c) || IsThaiCharacter(c))
                return BidiCharacterType.LeftToRight;

            // 欧洲数字
            if (c >= '0' && c <= '9')
                return BidiCharacterType.EuropeanNumberTerminator;

            // 阿拉伯数字（U+0660 - U+0669）
            if (c >= '\u0660' && c <= '\u0669')
                return BidiCharacterType.ArabicNumber;

            // 波斯数字（U+06F0 - U+06F9）
            if (c >= '\u06F0' && c <= '\u06F9')
                return BidiCharacterType.ArabicNumber;

            // 通用分隔符
            if (c == ',' || c == ';' || c == ':' || c == '!' || c == '?' ||
                c == '\u00AB' || c == '\u00BB')
                return BidiCharacterType.CommonSeparator;

            // 段落分隔符
            if (c == '\n' || c == '\r' || c == '\u2029')
                return BidiCharacterType.ParagraphSeparator;

            // 空白
            if (char.IsWhiteSpace(c))
                return BidiCharacterType.WhiteSpace;

            // 默认中性
            return BidiCharacterType.Neutral;
        }

        /// <summary>判断字符是否是强 RTL 字符</summary>
        public static bool IsRightToLeft(char c)
        {
            return IsArabicCharacter(c) || IsHebrewCharacter(c);
        }

        /// <summary>判断字符是否是强 LTR 字符</summary>
        public static bool IsLeftToRight(char c)
        {
            return Classify(c) == BidiCharacterType.LeftToRight;
        }

        /// <summary>阿拉伯语字符范围</summary>
        public static bool IsArabicCharacter(char c) =>
            (c >= '\u0600' && c <= '\u06FF') ||   // Arabic
            (c >= '\u0750' && c <= '\u077F') ||   // Arabic Supplement
            (c >= '\uFB50' && c <= '\uFDFF') ||   // Arabic Presentation Forms-A
            (c >= '\uFE70' && c <= '\uFEFF') ||   // Arabic Presentation Forms-B
            (c >= '\u08A0' && c <= '\u08FF');     // Arabic Extended-A

        /// <summary>希伯来语字符范围</summary>
        public static bool IsHebrewCharacter(char c) =>
            (c >= '\u0590' && c <= '\u05FF') ||   // Hebrew
            (c >= '\uFB1D' && c <= '\uFB4F');     // Hebrew Presentation Forms

        /// <summary>拉丁语字符范围</summary>
        public static bool IsLatinCharacter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '\u00C0' && c <= '\u024F') ||   // Latin Extended
            (c >= '\u1E00' && c <= '\u1EFF');     // Latin Extended Additional

        /// <summary>CJK 字符范围</summary>
        public static bool IsCJKCharacter(char c) =>
            (c >= '\u4E00' && c <= '\u9FFF') ||   // CJK Unified Ideographs
            (c >= '\u3040' && c <= '\u309F') ||   // Hiragana
            (c >= '\u30A0' && c <= '\u30FF') ||   // Katakana
            (c >= '\uAC00' && c <= '\uD7AF') ||   // Hangul Syllables
            (c >= '\u3400' && c <= '\u4DBF');     // CJK Extensions A

        /// <summary>西里尔语字符范围</summary>
        public static bool IsCyrillicCharacter(char c) =>
            (c >= '\u0400' && c <= '\u04FF') ||
            (c >= '\u0500' && c <= '\u052F');

        /// <summary>希腊语字符范围</summary>
        public static bool IsGreekCharacter(char c) =>
            (c >= '\u0370' && c <= '\u03FF') ||
            (c >= '\u1F00' && c <= '\u1FFF');

        /// <summary>泰语字符范围</summary>
        public static bool IsThaiCharacter(char c) =>
            (c >= '\u0E00' && c <= '\u0E7F');
    }

    // ══════════════════════════════════════════════════════════════════
    // BidiTextAnalyzer —— 双向文本分析器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 双向文本分析器。检测文本的基线方向，
    /// 实现 Unicode 双向算法的简化版本。
    /// </summary>
    public static class BidiTextAnalyzer
    {
        /// <summary>
        /// 检测文本的基线方向。
        /// 根据第一个强方向性字符决定整体方向。
        /// </summary>
        public static TextDirectionType DetectDirection(string text)
        {
            if (string.IsNullOrEmpty(text)) return TextDirectionType.LeftToRight;

            foreach (char c in text)
            {
                if (BidiCharacterClassifier.IsRightToLeft(c))
                    return TextDirectionType.RightToLeft;
                if (BidiCharacterClassifier.IsLeftToRight(c))
                    return TextDirectionType.LeftToRight;
            }

            return TextDirectionType.LeftToRight;
        }

        /// <summary>
        /// 简化的双向文本重排。
        /// 将混合方向文本按运行方向分组，并返回方向运行序列。
        /// 每个运行包含一段连续同方向文本。
        /// </summary>
        public static List<BidiRun> AnalyzeRuns(string text, TextDirectionType baseDirection)
        {
            var runs = new List<BidiRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            TextDirectionType currentRunDirection = baseDirection;
            int runStart = 0;

            for (int i = 0; i < text.Length; i++)
            {
                var charDir = GetEffectiveDirection(text[i]);
                var whitespace = char.IsWhiteSpace(text[i]);

                if (!whitespace && charDir != currentRunDirection)
                {
                    if (i > runStart)
                    {
                        runs.Add(new BidiRun
                        {
                            Text = text.Substring(runStart, i - runStart),
                            Direction = currentRunDirection,
                            StartIndex = runStart,
                            Length = i - runStart
                        });
                    }
                    runStart = i;
                    currentRunDirection = charDir;
                }
            }

            // 最后一个运行
            if (runStart < text.Length)
            {
                runs.Add(new BidiRun
                {
                    Text = text.Substring(runStart),
                    Direction = currentRunDirection,
                    StartIndex = runStart,
                    Length = text.Length - runStart
                });
            }

            return runs;
        }

        /// <summary>获取字符的有效方向</summary>
        private static TextDirectionType GetEffectiveDirection(char c)
        {
            return BidiCharacterClassifier.IsRightToLeft(c)
                ? TextDirectionType.RightToLeft
                : TextDirectionType.LeftToRight;
        }

        /// <summary>
        /// 简化的双向算法：在 RTL 基础方向下重排文本。
        /// 将 LTR 和 RTL 段按视觉顺序排列。
        /// </summary>
        public static string ReorderVisual(string text, TextDirectionType baseDirection)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (baseDirection == TextDirectionType.LeftToRight) return text;

            // RTL 基础方向：反转整个字符串
            // （简化实现，完整实现需要处理数字和嵌入 LTR 文本）
            var chars = text.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        /// <summary>检查文本是否包含 RTL 字符</summary>
        public static bool ContainsRTL(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Any(c => BidiCharacterClassifier.IsRightToLeft(c));
        }

        /// <summary>检查文本是否包含 LTR 字符</summary>
        public static bool ContainsLTR(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Any(c => BidiCharacterClassifier.IsLeftToRight(c));
        }

        /// <summary>检查文本是否为纯 RTL</summary>
        public static bool IsPureRTL(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.All(c =>
                BidiCharacterClassifier.IsRightToLeft(c) ||
                char.IsWhiteSpace(c) ||
                char.IsPunctuation(c) ||
                (c >= '0' && c <= '9'));
        }
    }

    /// <summary>双向文本运行（一段连续同方向文本）</summary>
    public sealed class BidiRun
    {
        /// <summary>文本内容</summary>
        public string Text { get; set; } = "";
        /// <summary>方向</summary>
        public TextDirectionType Direction { get; set; }
        /// <summary>在原文中的起始索引</summary>
        public int StartIndex { get; set; }
        /// <summary>长度</summary>
        public int Length { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════
    // NumberConverter —— 数字系统转换器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 数字系统转换器。在西方阿拉伯数字、印度-阿拉伯数字、
    /// 波斯数字、天城文数字、泰文数字之间转换。
    /// </summary>
    public static class NumberConverter
    {
        /// <summary>西方阿拉伯数字</summary>
        private static readonly char[] WesternDigits = { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };

        /// <summary>东方阿拉伯数字（阿拉伯语使用）</summary>
        private static readonly char[] EasternArabicDigits = { '\u0660', '\u0661', '\u0662', '\u0663', '\u0664', '\u0665', '\u0666', '\u0667', '\u0668', '\u0669' };

        /// <summary>波斯/印度数字</summary>
        private static readonly char[] PersianDigits = { '\u06F0', '\u06F1', '\u06F2', '\u06F3', '\u06F4', '\u06F5', '\u06F6', '\u06F7', '\u06F8', '\u06F9' };

        /// <summary>天城文数字（印地语）</summary>
        private static readonly char[] DevanagariDigits = { '\u0966', '\u0967', '\u0968', '\u0969', '\u096A', '\u096B', '\u096C', '\u096D', '\u096E', '\u096F' };

        /// <summary>泰文数字</summary>
        private static readonly char[] ThaiDigits = { '\u0E50', '\u0E51', '\u0E52', '\u0E53', '\u0E54', '\u0E55', '\u0E56', '\u0E57', '\u0E58', '\u0E59' };

        /// <summary>中日韩数字</summary>
        private static readonly char[] CJKDigits = { '\u96F6', '\u4E00', '\u4E8C', '\u4E09', '\u56DB', '\u4E94', '\u516D', '\u4E03', '\u516B', '\u4E5D' };

        /// <summary>根据语言代码获取默认数字系统</summary>
        public static NumberSystem GetSystemForLocale(string localeTag)
        {
            var langCode = localeTag.Split('-')[0].ToLowerInvariant();
            return langCode switch
            {
                "ar" => NumberSystem.EasternArabic,
                "fa" => NumberSystem.Persian,
                "hi" or "mr" or "ne" => NumberSystem.Devanagari,
                "th" => NumberSystem.Thai,
                "zh" or "ja" or "ko" => NumberSystem.CJK,
                _ => NumberSystem.WesternArabic
            };
        }

        /// <summary>转换数字字符串到指定系统</summary>
        public static string ConvertDigits(string text, NumberSystem targetSystem)
        {
            if (string.IsNullOrEmpty(text)) return text;

            char[] targetDigits = targetSystem switch
            {
                NumberSystem.EasternArabic => EasternArabicDigits,
                NumberSystem.Persian => PersianDigits,
                NumberSystem.Devanagari => DevanagariDigits,
                NumberSystem.Thai => ThaiDigits,
                NumberSystem.CJK => CJKDigits,
                _ => WesternDigits
            };

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9')
                    sb.Append(targetDigits[c - '0']);
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>转换数字到语言对应的数字系统</summary>
        public static string ConvertDigitsForLocale(string text, string localeTag)
        {
            var system = GetSystemForLocale(localeTag);
            return ConvertDigits(text, system);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // RTLLayoutHelper —— RTL 布局辅助工具
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// RTL 布局辅助工具。在 RTL 模式下翻转 UI 坐标和布局。
    /// 用于 Widget 对齐、间距、位置镜像等。
    /// </summary>
    public static class RTLLayoutHelper
    {
        /// <summary>水平镜像 X 坐标（在指定容器宽度内）</summary>
        public static float MirrorX(float x, float containerWidth)
        {
            return containerWidth - x;
        }

        /// <summary>水平镜像矩形</summary>
        public static System.Numerics.Vector4 MirrorRect(
            float x, float y, float width, float height,
            float containerWidth)
        {
            return new System.Numerics.Vector4(
                containerWidth - x - width,
                y,
                width,
                height);
        }

        /// <summary>镜像对齐方式</summary>
        public static HorizontalAlignment MirrorAlignment(HorizontalAlignment alignment)
        {
            return alignment switch
            {
                HorizontalAlignment.Left => HorizontalAlignment.Right,
                HorizontalAlignment.Right => HorizontalAlignment.Left,
                HorizontalAlignment.Center => HorizontalAlignment.Center,
                HorizontalAlignment.Stretch => HorizontalAlignment.Stretch,
                _ => alignment
            };
        }

        /// <summary>镜像边距</summary>
        public static Thickness MirrorThickness(Thickness thickness)
        {
            return new Thickness
            {
                Left = thickness.Right,
                Right = thickness.Left,
                Top = thickness.Top,
                Bottom = thickness.Bottom
            };
        }

        /// <summary>镜像边距（Vector4 版本：Left, Top, Right, Bottom）</summary>
        public static Vector4 MirrorMargin(Vector4 margin)
        {
            return new Vector4(margin.Z, margin.Y, margin.X, margin.W);
        }

        /// <summary>镜像水平偏移</summary>
        public static float MirrorOffset(float offset, float elementWidth)
        {
            return -offset - elementWidth;
        }
    }

    /// <summary>水平对齐枚举</summary>
    public enum HorizontalAlignment
    {
        Left,
        Right,
        Center,
        Stretch
    }

    /// <summary>边距（四方向独立）</summary>
    public sealed class Thickness
    {
        public float Left { get; set; }
        public float Top { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════
    // RTLTextProcessor —— RTL 文本处理器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// RTL 文本处理器。提供文本方向处理、数字转换、布局提示等完整功能。
    /// </summary>
    public sealed class RTLTextProcessor
    {
        private static readonly Lazy<RTLTextProcessor> _lazy =
            new(() => new RTLTextProcessor(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局单例</summary>
        public static RTLTextProcessor Instance => _lazy.Value;

        /// <summary>是否启用 RTL 处理</summary>
        public bool RTLEnabled { get; set; } = true;

        /// <summary>当前文本方向</summary>
        public TextDirectionType CurrentDirection { get; set; } = TextDirectionType.LeftToRight;

        private RTLTextProcessor() { }

        /// <summary>更新当前方向（根据语言自动检测）</summary>
        public void UpdateDirection(string localeTag)
        {
            var manager = LocalizationManager.Instance;
            if (manager.TryGetLocale(localeTag, out var locale))
            {
                CurrentDirection = locale.Direction == TextDirectionType.RightToLeft
                    ? TextDirectionType.RightToLeft
                    : TextDirectionType.LeftToRight;
            }
        }

        /// <summary>处理文本：方向转换 + 数字转换</summary>
        public string ProcessText(string text, string localeTag,
                                 bool convertNumbers = true)
        {
            if (!RTLEnabled || string.IsNullOrEmpty(text))
                return text;

            var direction = DetectTextDirection(text, localeTag);
            var processed = text;

            // RTL 方向下重排文本
            if (direction == TextDirectionType.RightToLeft)
            {
                processed = BidiTextAnalyzer.ReorderVisual(processed, direction);
            }

            // 数字转换
            if (convertNumbers)
            {
                processed = NumberConverter.ConvertDigitsForLocale(processed, localeTag);
            }

            return processed;
        }

        /// <summary>检测文本方向（结合语言设置和文本内容）</summary>
        public TextDirectionType DetectTextDirection(string text, string localeTag)
        {
            var manager = LocalizationManager.Instance;
            if (manager.TryGetLocale(localeTag, out var locale) &&
                locale.Direction == TextDirectionType.RightToLeft)
            {
                // RTL 语言中，默认文本方向为 RTL
                // 但如果文本全是 LTR 字符（如纯英文），则使用 LTR
                if (!BidiTextAnalyzer.ContainsRTL(text) && BidiTextAnalyzer.ContainsLTR(text))
                    return TextDirectionType.LeftToRight;

                return TextDirectionType.RightToLeft;
            }

            return BidiTextAnalyzer.DetectDirection(text);
        }

        /// <summary>获取段落方向</summary>
        public ParagraphDirection GetParagraphDirection(string text, string localeTag)
        {
            var manager = LocalizationManager.Instance;
            if (manager.TryGetLocale(localeTag, out var locale) &&
                locale.Direction == TextDirectionType.RightToLeft)
            {
                return ParagraphDirection.RightToLeft;
            }

            var dir = BidiTextAnalyzer.DetectDirection(text);
            return dir == TextDirectionType.RightToLeft
                ? ParagraphDirection.RightToLeft
                : ParagraphDirection.LeftToRight;
        }

        /// <summary>为 UI Widget 应用 RTL 调整</summary>
        public RTLLayoutAdjustment ComputeLayoutAdjustment(
            string localeTag, float containerWidth)
        {
            var adjustment = new RTLLayoutAdjustment();
            var manager = LocalizationManager.Instance;

            if (manager.TryGetLocale(localeTag, out var locale) &&
                locale.Direction == TextDirectionType.RightToLeft)
            {
                adjustment.IsRTL = true;
                adjustment.ContainerWidth = containerWidth;
                adjustment.TextDirection = TextDirectionType.RightToLeft;
            }

            return adjustment;
        }
    }

    /// <summary>
    /// RTL 布局调整信息。UI 系统根据此信息自动调整 Widget 位置和大小。
    /// </summary>
    public sealed class RTLLayoutAdjustment
    {
        /// <summary>是否处于 RTL 模式</summary>
        public bool IsRTL { get; set; }

        /// <summary>容器宽度（用于坐标镜像）</summary>
        public float ContainerWidth { get; set; }

        /// <summary>文本方向</summary>
        public TextDirectionType TextDirection { get; set; } = TextDirectionType.LeftToRight;

        /// <summary>数字系统</summary>
        public NumberSystem NumberSystem { get; set; } = NumberSystem.WesternArabic;
    }
}
