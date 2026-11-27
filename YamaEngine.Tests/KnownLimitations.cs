using System;
using System.Collections.Generic;
using System.Linq;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 已知限制看板（2026-11-25 云端 CI 实测基线，第九轮：3 条 → 0 条，全部收敛）。
    ///
    /// 1. 只登记「自研实现与第三方参考工具的一致性」尚未达标的验证用例；
    /// 2. 每条必须带 实测值 / 目标值 / 达标路径 / 证据 CI run；
    /// 3. 达标后立即移除，让用例回归真实门禁；
    /// 4. 禁止用吞异常、降低阈值或 Compile Remove 制造假绿。
    ///
    /// 已修：FLAC channelAssign（第二轮）、Vorbis MDCT 归一化（第四轮）、
    /// MP3 gapless 口径（第九轮）。
    /// 第五/六轮曾把 MP3 归因为「解码器固有延迟 + 末帧 bit reservoir 畸变」，
    /// 第九轮四组云端数值诊断证伪并给出真因：enc_delay 被重复扣减，窗口整体
    /// 后移 576 样本。详见 docs/AUDIO_DECODER_GAPS.md 与 docs/AUDIO_MP3_GAPLESS_FIX.md。
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

        /// <summary>
        /// 第九轮（run 45991271018）已清空：MP3 gapless 口径重复扣减 enc_delay 真修后，
        /// TestMp3SineDecode 回归真实门禁并通过（corr=0.999897 / maxDiff=0.023485）。
        /// 列表保留为空结构，若将来新增隔离项，SelfCheckTests 的上界 3 仍会约束它。
        /// </summary>
        private static readonly List<Gap> _gaps = new List<Gap>
        {
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
