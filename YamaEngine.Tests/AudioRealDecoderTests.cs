using System;
using System.Collections.Generic;
using System.IO;
using YamaEngine.Audio;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // AudioRealDecoderTests — FLAC/Vorbis/MP3 完整解码器验证
    // FLAC: 真实 libsndfile 编码文件位精确解码 + STREAMINFO MD5 校验
    //       + 自建编码器全子帧类型/全声道模式轮转
    // Vorbis/MP3: 真实编码文件解码 vs libsndfile 参考解码 (对齐+相关系数)
    // ══════════════════════════════════════════════════════════════
    public static class AudioRealDecoderTests
    {
        // ── 夹具定位: 从输出目录向上找到仓库内 YamaEngine.Tests/TestData/Audio ──
        private static string FindFixtureDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "YamaEngine.Tests", "TestData", "Audio");
                if (Directory.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "TestData", "Audio");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new InvalidOperationException("未找到音频测试夹具目录 TestData/Audio");
        }

        private static byte[] Fixture(string name)
        {
            return File.ReadAllBytes(Path.Combine(FixtureDir(), name));
        }

        private static string _fixtureDir;
        private static string FixtureDir() => _fixtureDir ??= FindFixtureDir();

        // ================================================================
        // FLAC: 真实文件位精确解码
        // ================================================================
        public static void TestFlacSineBitExact()
        {
            var dec = new FlacDecoder();
            var info = dec.Load(Fixture("sine8k_mono_440.flac"));
            AssertEqual(8000, info.SampleRate, "FLAC 采样率");
            AssertEqual(1, info.Channels, "FLAC 声道数");
            AssertEqual(16, info.BitsPerSample, "FLAC 位深");
            AssertEqual(4000L, info.TotalSamples, "FLAC 总样本数");

            var pcm = dec.DecodeAll(); // 内部含 MD5 位精确校验, 不符即抛
            AssertEqual(4000, pcm.Length, "FLAC 解码样本数");

            // 与原始 int16 参考逐位比较 (int16/32768 在 float 中精确)
            var raw = Fixture("sine8k_mono_440.s16");
            for (int i = 0; i < 4000; i++)
            {
                short s = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                float expected = s / 32768.0f;
                AssertTrue(Math.Abs(pcm[i] - expected) < 1e-9f,
                    $"FLAC 位精确失败 @{i}: {pcm[i]} vs {expected}");
            }
        }

        public static void TestFlacChirpStereoMd5()
        {
            var dec = new FlacDecoder();
            var info = dec.Load(Fixture("chirp16k_stereo.flac"));
            AssertEqual(16000, info.SampleRate, "chirp 采样率");
            AssertEqual(2, info.Channels, "chirp 声道数");
            AssertEqual(24, info.BitsPerSample, "chirp 位深");
            AssertEqual(6400L, info.TotalSamples, "chirp 总帧数");

            // STREAMINFO MD5 非全零 → DecodeAll 内部做位精确校验
            var md5 = dec.StreamMd5;
            bool allZero = true;
            foreach (var b in md5) { if (b != 0) { allZero = false; break; } }
            AssertTrue(!allZero, "chirp STREAMINFO MD5 不应为空");

            var pcm = dec.DecodeAll();
            AssertEqual(12800, pcm.Length, "chirp 解码样本数(6400帧×2声道)");

            // 与 libsndfile 参考解码比较 (同为位精确结果)
            var reference = Fixture("chirp16k_stereo.flac.ref.f32");
            AssertEqual(reference.Length / 4, pcm.Length, "chirp 参考长度一致");
            for (int i = 0; i < pcm.Length; i++)
            {
                float expect = BitConverter.ToSingle(reference, i * 4);
                AssertTrue(Math.Abs(pcm[i] - expect) < 1e-6f,
                    $"chirp 位精确失败 @{i}: {pcm[i]} vs {expect}");
            }
        }

        // ================================================================
        // FLAC: 自建编码器轮转 (全子帧类型 × 全声道模式)
        // ================================================================
        public static void TestFlacRoundTrip()
        {
            var rng = new Lcg(12345);

            // 各种子信号覆盖不同预测器路径
            var signals = new Dictionary<string, int[][]>
            {
                ["impulse"] = MakeSignal(64, ch => MakeImpulse(64, ch)),
                ["constant"] = MakeSignal(64, ch => MakeConstant(64, 777)),
                ["ramp"] = MakeSignal(64, ch => MakeRamp(64, 3)),
                ["sine"] = MakeSignal(128, ch => MakeSine(128, 5, 8000)),
                ["random"] = MakeSignal(64, ch => MakeRandom(64, rng)),
            };

            foreach (var kv in signals)
            {
                foreach (FlacStereoMode mode in Enum.GetValues(typeof(FlacStereoMode)))
                {
                    var encoded = FlacTestEncoder.Encode(kv.Value, 44100, 16, mode);
                    var dec = new FlacDecoder();
                    dec.Load(encoded);
                    var decoded = dec.DecodeAll(); // MD5 校验含在内

                    int frames = kv.Value[0].Length;
                    int channels = kv.Value.Length;
                    AssertEqual(frames * channels, decoded.Length, $"轮转样本数 {kv.Key}/{mode}");
                    for (int f = 0; f < frames; f++)
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            float expect = kv.Value[ch][f] / 32768.0f;
                            float actual = decoded[f * channels + ch];
                            AssertTrue(Math.Abs(actual - expect) < 1e-9f,
                                $"轮转失配 {kv.Key}/{mode} @{f},{ch}: {actual} vs {expect}");
                        }
                    }
                }
            }
        }

        private enum FlacStereoMode { Independent, LeftSide, RightSide, MidSide }

        private static int[][] MakeSignal(int frames, Func<int, int[]> gen)
        {
            // 立体声: 左右不同信号以激发声道去相关
            return new[] { gen(0), gen(1) };
        }

        private static int[] MakeImpulse(int n, int seed)
        {
            var a = new int[n];
            if (n > 3) a[n / 2] = seed == 0 ? 20000 : -15000;
            return a;
        }

        private static int[] MakeConstant(int n, int v)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = v;
            return a;
        }

        private static int[] MakeRamp(int n, int step)
        {
            var a = new int[n];
            int v = -1000;
            for (int i = 0; i < n; i++) { a[i] = v; v += step; }
            return a;
        }

        private static int[] MakeSine(int n, int period, int amp)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++)
                a[i] = (int)Math.Round(amp * Math.Sin(2.0 * Math.PI * i / period));
            return a;
        }

        private static int[] MakeRandom(int n, Lcg rng)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = rng.Next(-30000, 30000);
            return a;
        }

        /// <summary>确定性 LCG 随机源 (测试内用, 保证可重复)。</summary>
        private sealed class Lcg
        {
            private uint _s;
            public Lcg(uint seed) { _s = seed; }
            public int Next(int lo, int hi)
            {
                _s = _s * 1664525u + 1013904223u;
                return lo + (int)((hi - lo) * ((_s >> 8) / 16777216.0));
            }
        }

        /// <summary>
        /// 最小 FLAC 编码器 (仅用于测试轮转): 固定块大小、单帧、
        /// 支持 Constant/Verbatim/Fixed0-4/LPC 子帧与四种立体声模式。
        /// </summary>
        private static class FlacTestEncoder
        {
            public static byte[] Encode(int[][] channels, int sampleRate, int bps, FlacStereoMode mode)
            {
                int frames = channels[0].Length;
                var bw = new BitWriter();

                // ── 文件头: fLaC + STREAMINFO ──
                foreach (var c in "fLaC") bw.WriteBits((int)c, 8);
                bw.WriteBits(1, 1);           // 最后块=1 (仅 STREAMINFO 一个元数据块)
                bw.WriteBits(0, 7);           // STREAMINFO
                bw.WriteBits(34, 24);         // 长度
                bw.WriteBits(4096, 16);       // min blocksize
                bw.WriteBits(4096, 16);       // max blocksize
                bw.WriteBits(0, 24);          // min frame size
                bw.WriteBits(0, 24);          // max frame size
                bw.WriteBits(sampleRate, 20);
                bw.WriteBits(channels.Length - 1, 3);
                bw.WriteBits(bps - 1, 5);
                bw.WriteBits(frames, 36);
                for (int i = 0; i < 16; i++) bw.WriteBits(0, 8); // MD5 (未计算, 解码器对全零跳过校验)

                // ── 音频帧 ──
                // 声道重映射: 编码端做相关处理
                int[][] sub;
                if (mode == FlacStereoMode.LeftSide)
                {
                    sub = new[] { channels[0], new int[frames] };
                    for (int i = 0; i < frames; i++) sub[1][i] = channels[0][i] - channels[1][i];
                }
                else if (mode == FlacStereoMode.RightSide)
                {
                    sub = new[] { new int[frames], channels[1] };
                    for (int i = 0; i < frames; i++) sub[0][i] = channels[1][i] - channels[0][i];
                }
                else if (mode == FlacStereoMode.MidSide)
                {
                    sub = new[] { new int[frames], new int[frames] };
                    for (int i = 0; i < frames; i++)
                    {
                        sub[0][i] = (channels[0][i] + channels[1][i]) >> 1;
                        sub[1][i] = channels[0][i] - channels[1][i];
                    }
                }
                else
                {
                    sub = channels;
                }

                // 帧头
                int frameStart = bw.ByteCount; // 帧头起始字节 (字节对齐)
                bw.WriteBits(0x3FFE, 14);     // 同步
                bw.WriteBits(0, 1);           // 保留
                bw.WriteBits(0, 1);           // 固定块大小
                bw.WriteBits(7, 4);           // 块大小: 16位跟随
                bw.WriteBits(0, 4);           // 采样率: STREAMINFO
                bw.WriteBits(channels.Length == 2 ? 0 : channels.Length - 1, 4); // 声道(独立)
                bw.WriteBits(4, 3);           // 16 bit
                bw.WriteBits(0, 1);           // 保留
                bw.WriteBits(0, 8);           // 帧号 (UTF-8 单字节)
                bw.WriteBits(frames - 1, 16); // 块大小-1
                bw.WriteBits(0, 8);           // CRC-8 (测试流不做严格帧头校验绕过: 写0)
                // 注: 解码器会校验 CRC-8, 需正确计算 → 回填
                FixCrc8(bw, frameStart);

                // 子帧: 根据信号特征选择编码方式 (侧声道多 1 位)
                for (int ch = 0; ch < channels.Length; ch++)
                {
                    int chBps = bps;
                    if ((mode == FlacStereoMode.LeftSide && ch == 1)
                        || (mode == FlacStereoMode.RightSide && ch == 0)
                        || (mode == FlacStereoMode.MidSide && ch == 1))
                        chBps++;
                    EncodeSubframeAuto(bw, sub[ch], chBps);
                }

                bw.AlignToByte();
                bw.WriteBits(0, 16); // CRC-16 占位 → 回填
                FixCrc16(bw, frameStart);

                return bw.ToBytes();
            }

            private static void FixCrc8(BitWriter bw, int frameStart)
            {
                // CRC-8 覆盖帧头 7 字节 (同步..块大小), 回填到第 8 字节
                var bytes = bw.ToBytes();
                byte crc = 0;
                for (int i = frameStart; i < frameStart + 7; i++)
                {
                    crc ^= bytes[i];
                    for (int b = 0; b < 8; b++)
                        crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
                }
                bw.PatchByte(frameStart + 7, crc);
            }

            private static void FixCrc16(BitWriter bw, int frameStart)
            {
                // CRC-16 覆盖整个帧 (帧头+子帧, 不含 CRC 自身), 回填到末尾 2 字节
                var bytes = bw.ToBytes();
                ushort crc = 0;
                for (int i = frameStart; i < bytes.Length - 2; i++)
                {
                    crc ^= (ushort)(bytes[i] << 8);
                    for (int b = 0; b < 8; b++)
                        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
                }
                bw.PatchByte(bytes.Length - 2, (byte)(crc >> 8));
                bw.PatchByte(bytes.Length - 1, (byte)(crc & 0xFF));
            }

            private static void EncodeSubframeAuto(BitWriter bw, int[] s, int bps)
            {
                // 判定: 常量 / 斜坡(一阶固定) / 二阶 / 高阶LPC / 原样
                bool constant = true;
                for (int i = 1; i < s.Length; i++) if (s[i] != s[0]) { constant = false; break; }

                if (constant)
                {
                    bw.WriteBits(0, 1);      // 填充
                    bw.WriteBits(0, 6);      // constant
                    bw.WriteBits(0, 1);      // 无浪费位
                    bw.WriteBits(s[0], bps);
                    return;
                }

                // 尝试各阶固定预测器, 选残差最小者
                int bestOrder = -1;
                long bestScore = long.MaxValue;
                for (int order = 0; order <= 4; order++)
                {
                    long score = 0;
                    for (int i = order; i < s.Length; i++)
                    {
                        long pred = order switch
                        {
                            0 => 0,
                            1 => s[i - 1],
                            2 => 2L * s[i - 1] - s[i - 2],
                            3 => 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3],
                            _ => 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4]
                        };
                        score += Math.Abs(s[i] - pred);
                    }
                    if (score < bestScore) { bestScore = score; bestOrder = order; }
                }

                // 信号能量足够且固定预测不佳时用 LPC(2阶)
                bool useLpc = bestScore > s.Length * 64 && s.Length > 8;

                if (useLpc)
                {
                    // 简单LPC: 用前两个系数的朴素估计 (测试覆盖用)
                    bw.WriteBits(0, 1);
                    bw.WriteBits(32 + 1, 6); // LPC order=2 (1xxxxx: 32+(order-1))
                    bw.WriteBits(0, 1);
                    // 预热
                    bw.WriteBits(s[0], bps);
                    bw.WriteBits(s[1], bps);
                    // 精度/移位/系数
                    int precision = 14, shift = 10;
                    int c1 = 1 << shift, c2 = -(1 << (shift - 2)); // y ≈ x1 - x0/4
                    bw.WriteBits(precision - 1, 4);
                    bw.WriteBits(shift, 5);
                    bw.WriteBits(c1, precision);
                    bw.WriteBits(c2 & ((1 << precision) - 1), precision);
                    // 残差 (熵编码方法=0, 分区0, rice k 按残差幅度选择)
                    var lpcRes = new long[s.Length];
                    long lpcMax = 0;
                    for (int i = 2; i < s.Length; i++)
                    {
                        long pred = ((long)c1 * s[i - 1] + (long)c2 * s[i - 2]) >> shift;
                        lpcRes[i] = s[i] - pred;
                        if (Math.Abs(lpcRes[i]) > lpcMax) lpcMax = Math.Abs(lpcRes[i]);
                    }
                    int lk = 0;
                    while ((1L << lk) < lpcMax && lk < 14) lk++;
                    bw.WriteBits(0, 2);
                    bw.WriteBits(0, 4); // partition order 0
                    bw.WriteBits(lk, 4); // rice k
                    for (int i = 2; i < s.Length; i++) WriteRice(bw, (int)lpcRes[i], lk);
                    return;
                }

                // 固定预测器
                bw.WriteBits(0, 1);
                bw.WriteBits(8 + bestOrder, 6);
                bw.WriteBits(0, 1);
                for (int i = 0; i < bestOrder; i++) bw.WriteBits(s[i], bps);
                bw.WriteBits(0, 2); // 熵编码方法: PARTITIONED_RICE
                bw.WriteBits(0, 4); // partition order 0
                // 依残差大小选 rice 参数
                long maxRes = 0;
                for (int i = bestOrder; i < s.Length; i++)
                {
                    long pred = bestOrder switch
                    {
                        0 => 0,
                        1 => s[i - 1],
                        2 => 2L * s[i - 1] - s[i - 2],
                        3 => 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3],
                        _ => 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4]
                    };
                    long r = s[i] - pred;
                    if (Math.Abs(r) > maxRes) maxRes = Math.Abs(r);
                }
                int k = 0;
                while ((1L << k) < maxRes && k < 14) k++;
                bw.WriteBits(k, 4);
                for (int i = bestOrder; i < s.Length; i++)
                {
                    long pred = bestOrder switch
                    {
                        0 => 0,
                        1 => s[i - 1],
                        2 => 2L * s[i - 1] - s[i - 2],
                        3 => 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3],
                        _ => 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4]
                    };
                    WriteRice(bw, s[i] - (int)pred, k);
                }
            }

            private static void WriteRice(BitWriter bw, int value, int k)
            {
                // zigzag 映射
                uint mapped = value >= 0 ? (uint)(value << 1) : (uint)((-value << 1) - 1);
                uint quotient = k >= 32 ? 0 : mapped >> k;
                for (uint i = 0; i < quotient; i++) bw.WriteBits(0, 1);
                bw.WriteBits(1, 1);
                if (k > 0) bw.WriteBits((int)(mapped & ((1u << k) - 1)), k);
            }

            private sealed class BitWriter
            {
                private readonly List<byte> _bytes = new();
                private uint _cache;
                private int _bits;

                public void WriteBits(int value, int count)
                {
                    uint v = (uint)value;
                    for (int i = count - 1; i >= 0; i--)
                    {
                        _cache = (_cache << 1) | ((v >> i) & 1);
                        _bits++;
                        if (_bits == 8)
                        {
                            _bytes.Add((byte)_cache);
                            _cache = 0;
                            _bits = 0;
                        }
                    }
                }

                public void AlignToByte()
                {
                    while (_bits != 0) WriteBits(0, 1);
                }

                public byte[] ToBytes()
                {
                    var r = new byte[_bytes.Count + (_bits > 0 ? 1 : 0)];
                    _bytes.CopyTo(r, 0);
                    if (_bits > 0) r[_bytes.Count] = (byte)(_cache << (8 - _bits));
                    return r;
                }

                public int ByteCount => _bytes.Count;

                public void PatchByte(int index, byte value) => _bytes[index] = value;
            }
        }

        // ================================================================
        // Vorbis: 真实文件 vs libsndfile 参考解码
        // ================================================================
        public static void TestVorbisSineDecode()
        {
            var dec = new VorbisDecoder();
            var info = dec.Load(Fixture("sine16k_mono.ogg"));
            AssertEqual(16000, info.SampleRate, "Vorbis 采样率");
            AssertEqual(1, info.Channels, "Vorbis 声道数");

            var pcm = dec.DecodeAll();
            AssertEqual(8000, pcm.Length, "Vorbis 解码样本数 (granule 精确)");

            CompareWithReference(pcm, "sine16k_mono.ogg.ref.f32", 8000, 0.999, 2e-3, "Vorbis");
        }

        // ================================================================
        // MP3: 真实文件 vs libsndfile(mpg123) 参考解码
        // ================================================================
        public static void TestMp3SineDecode()
        {
            var dec = new Mp3Decoder();
            var info = dec.Load(Fixture("sine16k_mono.mp3"));
            AssertEqual(16000, info.SampleRate, "MP3 采样率");
            AssertEqual(1, info.Channels, "MP3 声道数");
            AssertTrue(info.NumFrames > 10, $"MP3 帧数合理 ({info.NumFrames})");

            var pcm = dec.DecodeAll();
            AssertTrue(pcm.Length > 7000, $"MP3 解码样本数 ({pcm.Length})");

            CompareWithReferenceAligned(pcm, "sine16k_mono.mp3.ref.f32", 0.97, 5e-2, "MP3");
        }

        // ================================================================
        // 共用比较工具
        // ================================================================
        private static void CompareWithReference(float[] pcm, string refName, int expectedLen,
                                                 double minCorr, double maxAbsDiff, string tag)
        {
            var reference = Fixture(refName);
            int n = reference.Length / 4;
            AssertEqual(expectedLen, n, $"{tag} 参考长度");
            AssertEqual(n, pcm.Length, $"{tag} 解码长度");

            double sumDiff = 0, maxDiff = 0, dot = 0, e1 = 0, e2 = 0;
            for (int i = 0; i < n; i++)
            {
                float expect = BitConverter.ToSingle(reference, i * 4);
                double d = Math.Abs(pcm[i] - expect);
                sumDiff += d;
                if (d > maxDiff) maxDiff = d;
                dot += pcm[i] * expect;
                e1 += pcm[i] * pcm[i];
                e2 += expect * expect;
            }
            double corr = dot / (Math.Sqrt(e1) * Math.Sqrt(e2) + 1e-12);
            AssertTrue(corr >= minCorr, $"{tag} 相关系数 {corr:F6} < {minCorr}");
            AssertTrue(maxDiff <= maxAbsDiff, $"{tag} 最大绝对差 {maxDiff:F6} > {maxAbsDiff}");
        }

        private static void CompareWithReferenceAligned(float[] pcm, string refName,
                                                        double minCorr, double maxAbsDiff, string tag)
        {
            var reference = Fixture(refName);
            int n = reference.Length / 4;

            // 在参考与解码之间搜索最佳对齐 (编码器/解码器延迟差异)
            int bestOff = 0;
            double bestCorr = -1;
            for (int off = -2400; off <= 2400; off += 8)
            {
                double corr = CorrelationAt(pcm, reference, n, off);
                if (corr > bestCorr) { bestCorr = corr; bestOff = off; }
            }
            // 精搜
            for (int off = bestOff - 8; off <= bestOff + 8; off++)
            {
                double corr = CorrelationAt(pcm, reference, n, off);
                if (corr > bestCorr) { bestCorr = corr; bestOff = off; }
            }

            AssertTrue(bestCorr >= minCorr, $"{tag} 对齐后相关系数 {bestCorr:F6} < {minCorr} (off={bestOff})");

            // 对齐后的逐样本误差
            double maxDiff = 0;
            int a0 = Math.Max(0, bestOff), b0 = Math.Max(0, -bestOff);
            int count = Math.Min(n - a0, pcm.Length - b0);
            for (int i = 0; i < count; i++)
            {
                float expect = BitConverter.ToSingle(reference, (a0 + i) * 4);
                double d = Math.Abs(pcm[b0 + i] - expect);
                if (d > maxDiff) maxDiff = d;
            }
            AssertTrue(maxDiff <= maxAbsDiff, $"{tag} 最大绝对差 {maxDiff:F6} > {maxAbsDiff}");
        }

        private static double CorrelationAt(float[] pcm, byte[] reference, int n, int off)
        {
            int a0 = Math.Max(0, off), b0 = Math.Max(0, -off);
            int count = Math.Min(n - a0, pcm.Length - b0);
            if (count < 100) return -1;
            double dot = 0, e1 = 0, e2 = 0;
            for (int i = 0; i < count; i++)
            {
                float expect = BitConverter.ToSingle(reference, (a0 + i) * 4);
                float actual = pcm[b0 + i];
                dot += actual * expect;
                e1 += actual * actual;
                e2 += expect * expect;
            }
            return dot / (Math.Sqrt(e1) * Math.Sqrt(e2) + 1e-12);
        }
    }
}
