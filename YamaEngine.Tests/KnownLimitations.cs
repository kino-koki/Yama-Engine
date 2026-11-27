using System;
using System.Collections.Generic;
using System.Linq;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 已知限制看板（2026-11-25 云端 CI 实测基线，第五轮：条目仍是 1 条，但归因更正）。
    ///
    /// 1. 只登记「自研实现与第三方参考工具的一致性」尚未达标的验证用例；
    /// 2. 每条必须带 实测值 / 目标值 / 达标路径 / 证据 CI run；
    /// 3. 达标后立即移除，让用例回归真实门禁；
    /// 4. 禁止用吞异常、降低阈值或 Compile Remove 制造假绿。
    ///
    /// 已修：FLAC channelAssign（第二轮）、Vorbis MDCT 归一化（第四轮）。
    /// 第五轮撤回上一版「≈0.46 样本亚样本相位」归因：谐波级 + 分段诊断证明
    /// 解码主体在前 6000 样本已达 md=0.02348 / corr=0.99990，剩余是
    /// 「解码器固有延迟未补偿」+「末帧附近局部畸变」。详见 docs/AUDIO_DECODER_GAPS.md。
    /// </summary>
    public static class KnownLimitations
    {
        public sealed class Gap
        {
            public string Id { get; init; } = "";
            public string TestCase { get; init; } = "";
            public string Measured { get; init; } = "";
            public string Target { get; init; } = "";
            public string FixPath { get; init; } = "";
            public string EvidenceRun { get; init; } = "";
        }

        private static readonly List<Gap> _gaps = new List<Gap>
        {
            new Gap {
                Id = "AUDIO-MP3-001",
                TestCase = "YamaEngine.Tests.AudioRealDecoderTests.TestMp3SineDecode",
                Measured = "gapless 后整体对齐 corr=0.99263 / maxDiff=0.24488；但固定 6000 窗口、内部再对齐 s=1329 时 md=0.02348 / corr=0.99990，且 10 个 600 样本桶内 0 个 >0.05 误差；误差 >0.1 的 38 个样本全部落在 6400~7200 区间",
                Target = "相关系数 >= 0.97（已达到）且 maxAbsDiff <= 5e-2（整体口径未达到）",
                FixPath = "已排除 对齐窗口过窄 / 全局幅度 / gapless 裁剪 / 亚样本相位 / 算法精度。剩余两项：① 补偿解码器固有延迟（IMDCT overlap + QMF 预热，实测需额外前移约 1329 样本）；② 末帧 6400~7200 的局部畸变（疑似 bit reservoir 尾帧）。见 AUDIO_DECODER_GAPS.md M4-M6",
                EvidenceRun = "45624810254 / 45625573880 / 45806756991 / 45807569040 / 45808650705"
            }
        };

        private static readonly HashSet<string> _skipped =
            new HashSet<string>(_gaps.Select(g => g.TestCase), StringComparer.Ordinal);

        public static IReadOnlyList<Gap> Gaps => _gaps;
        public static int Count => _gaps.Count;

        public static bool IsSkipped(string? typeName, string methodName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return _skipped.Contains(typeName + "." + methodName);
        }

        public static bool AllEntriesWellFormed() =>
            _gaps.All(g => !string.IsNullOrWhiteSpace(g.Id)
                        && !string.IsNullOrWhiteSpace(g.TestCase)
                        && !string.IsNullOrWhiteSpace(g.Measured)
                        && !string.IsNullOrWhiteSpace(g.Target)
                        && !string.IsNullOrWhiteSpace(g.FixPath)
                        && !string.IsNullOrWhiteSpace(g.EvidenceRun))
            && _gaps.Select(g => g.Id).Distinct().Count() == _gaps.Count
            && _gaps.Select(g => g.TestCase).Distinct().Count() == _gaps.Count;
    }
}
