using System;
using System.IO;
using YamaEngine.Audio;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // AudioDecoderDepthTests — WavDecoder 工业级深度验证
    // 覆盖：损坏头防御（块大小越界/负）、除零（声道/块对齐）、
    //       输出 OOM 上限、data 块越界、范围解码溢出
    // ══════════════════════════════════════════════════════════════
    public static class AudioDecoderDepthTests
    {
        // ── 构造最小合法 WAV（PCM16 立体声） ──
        private static byte[] BuildValidPcm16Wav(int samplesPerChannel = 8)
        {
            int channels = 2;
            int bits = 16;
            int dataSize = samplesPerChannel * channels * (bits / 8);
            int total = 44 + dataSize;
            var b = new byte[total];
            // RIFF
            WriteAscii(b, 0, "RIFF");
            WriteU32(b, 4, (uint)(total - 8));
            WriteAscii(b, 8, "WAVE");
            // fmt
            WriteAscii(b, 12, "fmt ");
            WriteU32(b, 16, 16);
            WriteU16(b, 20, 1);          // PCM
            WriteU16(b, 22, (ushort)channels);
            WriteU32(b, 24, 44100);
            WriteU32(b, 28, (uint)(44100 * channels * 2));
            WriteU16(b, 32, (ushort)(channels * 2));
            WriteU16(b, 34, (ushort)bits);
            // data
            WriteAscii(b, 36, "data");
            WriteU32(b, 40, (uint)dataSize);
            return b;
        }

        private static void WriteAscii(byte[] b, int off, string s)
        {
            for (int i = 0; i < s.Length; i++) b[off + i] = (byte)s[i];
        }
        private static void WriteU16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)(v >> 8);
        }
        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF); b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        // ── 合法 WAV 正常解码 ──
        public static void TestWavValidDecode()
        {
            var dec = new WavDecoder();
            var info = dec.Load(BuildValidPcm16Wav());
            AssertEqual(44100, info.SampleRate, "sample rate");
            AssertEqual(2, info.Channels, "channels");
            AssertEqual(16, info.BitsPerSample, "bits");
            AssertEqual(8, info.NumSamples, "num samples");

            var samples = dec.DecodeAll();
            AssertEqual(8 * 2, samples.Length, "decode sample count");
            dec.Dispose();
        }

        // ── 过短文件 → InvalidDataException ──
        public static void TestWavTooShortThrows()
        {
            var dec = new WavDecoder();
            AssertThrows<InvalidDataException>(() => dec.Load(new byte[5]), "short file");
            dec.Dispose();
        }

        // ── 非 RIFF → InvalidDataException ──
        public static void TestWavMissingRiffThrows()
        {
            var b = new byte[12];
            WriteAscii(b, 0, "XXXX");
            var dec = new WavDecoder();
            AssertThrows<InvalidDataException>(() => dec.Load(b), "missing RIFF");
            dec.Dispose();
        }

        // ── data 块声明大小超出文件 → 抛异常（防 Position 越界/EndOfStream） ──
        public static void TestWavChunkSizeBeyondFileThrows()
        {
            var b = BuildValidPcm16Wav();
            // 把 data 块大小改成 0x7FFFFFFF（伪造巨大块）
            WriteU32(b, 40, 0x7FFFFFFF);
            var dec = new WavDecoder();
            AssertThrows<InvalidDataException>(() => dec.Load(b), "huge data chunk");
            dec.Dispose();
        }

        // ── fmt 声道数 = 0 → 除零防护 ──
        public static void TestWavZeroChannelsThrows()
        {
            var b = BuildValidPcm16Wav();
            WriteU16(b, 22, 0); // channels = 0
            var dec = new WavDecoder();
            AssertThrows<InvalidDataException>(() => dec.Load(b), "zero channels");
            dec.Dispose();
        }

        // ── 位深非法（如 12bit）→ 抛异常 ──
        public static void TestWavInvalidBitsThrows()
        {
            var b = BuildValidPcm16Wav();
            WriteU16(b, 34, 12); // bits = 12（不支持）
            var dec = new WavDecoder();
            AssertThrows<InvalidDataException>(() => dec.Load(b), "invalid bits");
            dec.Dispose();
        }

        // ── data 块偏移/大小与文件不符 → DecodeAll 抛异常 ──
        public static void TestWavDataBlockOutOfRangeThrows()
        {
            // 伪造一个能通过头解析但 data 超出文件的有效头
            var b = BuildValidPcm16Wav();
            var dec = new WavDecoder();
            var info = dec.Load(b);
            // 篡改 info 的 DataSize（通过反射不可行；改用重新解析——直接构造畸形）
            info.DataSize = int.MaxValue;
            info.DataOffset = 0;
            AssertThrows<InvalidDataException>(() => dec.DecodeAll(), "data block out of range");
            dec.Dispose();
        }

        // ── smpl 循环点：超大 numSampleLoops 不崩溃（钳制） ──
        public static void TestWavHugeLoopCountClamped()
        {
            // 构造含 smpl 块的 WAV：RIFF(12) + fmt(24) + smpl(8+36+24*2) + data(8+16)
            int numLoops = 2;
            int smplBodySize = 36 + 24 * numLoops;
            int dataHeader = 36 + 8 + smplBodySize;          // 128
            int total = dataHeader + 8 + 16;                 // 152
            var nb = new byte[total];
            WriteAscii(nb, 0, "RIFF");
            WriteU32(nb, 4, (uint)(total - 8));
            WriteAscii(nb, 8, "WAVE");
            WriteAscii(nb, 12, "fmt ");
            WriteU32(nb, 16, 16);
            WriteU16(nb, 20, 1);
            WriteU16(nb, 22, 2);
            WriteU32(nb, 24, 44100);
            WriteU32(nb, 28, 44100 * 4);
            WriteU16(nb, 32, 4);
            WriteU16(nb, 34, 16);
            // smpl 块: 头(8) + 固定字段(36) + numLoops*24
            int off = 36;
            WriteAscii(nb, off, "smpl");
            WriteU32(nb, off + 4, (uint)smplBodySize);
            int body = off + 8;
            // 固定 7 个 int (28 字节) 置 0
            for (int i = 0; i < 7; i++) WriteU32(nb, body + i * 4, 0);
            WriteU32(nb, body + 28, 0x7FFFFFFF); // numSampleLoops = 巨大
            WriteU32(nb, body + 32, 0);          // sampler data size
            // 2 个循环 (每个 24 字节)
            int loopPos = body + 36;
            for (int i = 0; i < numLoops; i++)
            {
                WriteU32(nb, loopPos + i * 24, 0);
                WriteU32(nb, loopPos + i * 24 + 4, 0);
                WriteU32(nb, loopPos + i * 24 + 8, 0);
                WriteU32(nb, loopPos + i * 24 + 12, 0);
                WriteU32(nb, loopPos + i * 24 + 16, 0);
                WriteU32(nb, loopPos + i * 24 + 20, 0);
            }
            // data 块
            WriteAscii(nb, dataHeader, "data");
            WriteU32(nb, dataHeader + 4, 16);

            var dec = new WavDecoder();
            var info = dec.Load(nb); // 不崩溃
            AssertTrue(info != null, "loaded with huge loop count clamped");
            dec.Dispose();
        }

        // ── 范围解码：负数 startSample → 参数异常 ──
        public static void TestWavDecodeRangeNegativeThrows()
        {
            var dec = new WavDecoder();
            dec.Load(BuildValidPcm16Wav());
            AssertThrows<ArgumentOutOfRangeException>(() => dec.DecodeRange(-1, 4), "negative start");
            dec.Dispose();
        }

        // ── 范围解码：startSample 超出样本数 → 返回空（不越界） ──
        public static void TestWavDecodeRangeBeyondEndEmpty()
        {
            var dec = new WavDecoder();
            dec.Load(BuildValidPcm16Wav());
            var r = dec.DecodeRange(999999, 4);
            AssertEqual(0, r.Length, "empty range");
            dec.Dispose();
        }

        // ── IMA ADPCM：块对齐 < 声道*4 → 抛异常（除零/负样本防护） ──
        public static void TestWavImaAdpcmBadBlockAlignThrows()
        {
            var dec = new WavDecoder();
            var info = dec.Load(BuildValidPcm16Wav());
            // 强制构造 ADPCM 场景：直接 Set 私有字段不可行，改用构造含 ADPCM fmt 的 WAV
            int channels = 2;
            int blockAlign = 4; // < channels*4 → 非法
            int dataSize = blockAlign;
            int total = 44 + dataSize;
            var b = new byte[total];
            WriteAscii(b, 0, "RIFF");
            WriteU32(b, 4, (uint)(total - 8));
            WriteAscii(b, 8, "WAVE");
            WriteAscii(b, 12, "fmt ");
            WriteU32(b, 16, 16);
            WriteU16(b, 20, 2);          // IMA ADPCM
            WriteU16(b, 22, (ushort)channels);
            WriteU32(b, 24, 44100);
            WriteU32(b, 28, 44100);
            WriteU16(b, 32, (ushort)blockAlign);
            WriteU16(b, 34, 4);
            WriteAscii(b, 36, "data");
            WriteU32(b, 40, (uint)dataSize);

            AssertThrows<InvalidDataException>(() => dec.Load(b), "bad IMA block align");
            dec.Dispose();
        }

        // ── DecodeAll 未加载 → InvalidOperationException ──
        public static void TestWavDecodeAllNotLoadedThrows()
        {
            var dec = new WavDecoder();
            AssertThrows<InvalidOperationException>(() => dec.DecodeAll(), "not loaded");
            dec.Dispose();
        }
    }
}
