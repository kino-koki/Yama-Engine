using System;
using System.Linq;
using Xunit;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 门禁元测试：证明 ABI 自检进入回归门禁，且已知限制看板字段完整、条目受控、
    /// 已修用例不会被悄悄藏回去。这是 CI 绿灯可信度的结构性校验。
    /// </summary>
    public class SelfCheckTests
    {
        [Fact]
        public void TestAbiSelfChecksAreDiscovered()
        {
            var cases = XunitAdapter.AllTestCases().ToArray();
            Assert.NotEmpty(cases);
            var abi = cases.Where(c => (string)c[0] == "YamaEngine.Tests.ScriptAbiTests").ToArray();
            Assert.Equal(7, abi.Length);
        }

        [Fact]
        public void TestKnownLimitationsAreExplicitAndMinimal()
        {
            // 第九轮：FLAC / Vorbis / MP3 全部真修并回归门禁，看板已清空为 0 条。
            // 上界 3 防止悄悄新增隔离项；下界 0 表示「零隔离」是合法且期望的状态。
            Assert.InRange(KnownLimitations.Count, 0, 3);
            Assert.True(KnownLimitations.AllEntriesWellFormed(), "每条差距必须带实测/目标/路径/证据");
            foreach (var gap in KnownLimitations.Gaps)
            {
                Assert.Contains(">= ", gap.Target);
                Assert.StartsWith("AUDIO-", gap.Id);
            }
            // 已修用例绝不能再被隔离（防止回归时把用例藏回去）
            Assert.False(KnownLimitations.IsSkipped(
                "YamaEngine.Tests.AudioRealDecoderTests", "TestMp3SineDecode"));
            Assert.False(KnownLimitations.IsSkipped(
                "YamaEngine.Tests.ScriptAbiTests", "TestScriptVariantRoundtrip"));
            // 已修用例绝不能再被隔离（防止回归时把用例藏回去）
            Assert.False(KnownLimitations.IsSkipped(
                "YamaEngine.Tests.AudioRealDecoderTests", "TestFlacRoundTrip"));
            Assert.False(KnownLimitations.IsSkipped(
                "YamaEngine.Tests.AudioRealDecoderTests", "TestVorbisSineDecode"));
        }

        [Fact]
        public void TestAbiChecksAreNotSilentlySkipped()
        {
            var cases = XunitAdapter.AllTestCases().ToArray();
            Assert.DoesNotContain(cases, c => (string)c[0] == "YamaEngine.Tests.ScriptAbiTests"
                                           && KnownLimitations.IsSkipped((string)c[0], (string)c[1]));
        }
    }
}
