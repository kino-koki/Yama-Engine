// ============================================================================
// YamaEngine Mp3Decoder.cs
// yama引擎 - MP3/MPEG音频 完整解码器 (Layer I/II/III, MPEG-1/2/2.5)
// 算法结构移植自公有领域实现 minimp3 (CC0, Copyright (c) 2019 Lion "lieff" Voronin),
// 数据表见 Mp3Tables.cs (源自 ISO/IEC 11172-3)。
// 实现: 帧头/边信息解析、位 reservoir、缩放因子解码、Huffman 快表解码、
//       重量化(pow4/3)、强度/中间-旁边立体声、重排序、抗混叠蝶形、
//       IMDCT(36/12点)、DCT-II 与多相合成滤波器组、Layer I/II 子带解码。
// 输出: 交错 float[-1,1)。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YamaEngine.Audio
{
    /// <summary>
    /// MP3解码器 - 完整实现 (MPEG-1/2/2.5 Layer I/II/III)。
    /// </summary>
    public sealed class Mp3Decoder
    {
        // ── 常量 ──
        private const int MaxBitreservoirBytes = 511;
        private const int MaxL3FramePayloadBytes = 1440;   // >= 320000/8/32000*1152
        private const int HdrSize = 4;
        private const int ShortBlockType = 2;
        private const int StopBlockType = 3;
        private const int ModeMono = 3;
        private const int ModeJointStereo = 1;
        private const int BitsDequantizerOut = -1;
        private const int MaxScf = 255 + BitsDequantizerOut * 4 - 210;   // 41
        private const int MaxScfi = (MaxScf + 3) & ~3;                   // 44
        private const int MaxFreeFormatFrameSize = 2304;
        private const int MaxFrameSyncMatches = 10;

        private static readonly int[] GHertz = { 44100, 48000, 32000 };

        // MPEG版本
        public enum MpegVersion { Mpeg1, Mpeg2, Mpeg25 }
        public enum MpegLayer { Layer1, Layer2, Layer3 }

        /// <summary>
        /// MP3文件信息
        /// </summary>
        public sealed class Mp3Info
        {
            public MpegVersion Version { get; set; }
            public MpegLayer Layer { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int Bitrate { get; set; }
            public int FrameSize { get; set; }
            public int SamplesPerFrame { get; set; }
            public long NumFrames { get; set; }
            public long NumSamples { get; set; }
            public double DurationSeconds { get; set; }
            // ID3标签
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Album { get; set; }
            public int Track { get; set; }
        }

        /// <summary>
        /// 单帧解码信息 (对应 minimp3 mp3dec_frame_info_t)。
        /// </summary>
        public sealed class FrameInfo
        {
            public int FrameBytes;     // 本帧总消耗字节数 (含头部/填充)
            public int FrameOffset;    // 帧头在输入中的偏移
            public int Channels;
            public int SampleRate;
            public int Layer;          // 1..3
            public int BitrateKbps;
        }

        // ── 解码器持久状态 (对应 mp3dec_t) ──
        private readonly byte[] _header = new byte[4];
        private int _freeFormatBytes;
        private int _reserv;
        private readonly byte[] _reservBuf = new byte[MaxBitreservoirBytes];
        private readonly float[] _qmfState = new float[15 * 2 * 32];
        private readonly float[][] _mdctOverlap = { new float[9 * 32], new float[9 * 32] };
        private readonly byte[] _maindata = new byte[MaxBitreservoirBytes + MaxL3FramePayloadBytes + 8];

        private byte[] _fileData;
        private Mp3Info _info;
        private int _frameOffset;

        public Mp3Info Info => _info;

        // ====================================================================
        // 帧头辅助 (对应 minimp3 HDR_* 宏; 全部以 (数据, 偏移) 形式)
        // ====================================================================

        private static bool HdrIsMono(byte[] d, int p) => (d[p + 3] & 0xC0) == 0xC0;
        private static bool HdrIsMsStereo(byte[] d, int p) => (d[p + 3] & 0xE0) == 0x60;
        private static bool HdrIsFreeFormat(byte[] d, int p) => (d[p + 2] & 0xF0) == 0;
        private static bool HdrIsCrc(byte[] d, int p) => (d[p + 1] & 1) == 0;
        private static int HdrGetStereoMode(byte[] d, int p) => (d[p + 3] >> 6) & 3;
        private static int HdrGetStereoModeExt(byte[] d, int p) => (d[p + 3] >> 4) & 3;
        private static int HdrGetLayer(byte[] d, int p) => (d[p + 1] >> 1) & 3;
        private static int HdrGetBitrateIdx(byte[] d, int p) => d[p + 2] >> 4;
        private static int HdrGetSampleRateIdx(byte[] d, int p) => (d[p + 2] >> 2) & 3;
        private static int HdrGetMySampleRateIdx(byte[] d, int p) =>
            HdrGetSampleRateIdx(d, p) + (((d[p + 1] >> 3) & 1) + ((d[p + 1] >> 4) & 1)) * 3;
        private static bool HdrIsFrame576(byte[] d, int p) => (d[p + 1] & 14) == 2;
        private static bool HdrIsLayer1(byte[] d, int p) => (d[p + 1] & 6) == 6;
        private static bool HdrTestMpeg1(byte[] d, int p) => (d[p + 1] & 0x8) != 0;
        private static bool HdrTestNotMpeg25(byte[] d, int p) => (d[p + 1] & 0x10) != 0;
        private static bool HdrTestIStereo(byte[] d, int p) => (d[p + 3] & 0x10) != 0;
        private static bool HdrTestMsStereo(byte[] d, int p) => (d[p + 3] & 0x20) != 0;
        private static bool HdrTestPadding(byte[] d, int p) => (d[p + 2] & 0x2) != 0;

        private static bool HdrValidAt(byte[] d, int p)
        {
            return d[p] == 0xff &&
                ((d[p + 1] & 0xF0) == 0xf0 || (d[p + 1] & 0xFE) == 0xe2) &&
                HdrGetLayer(d, p) != 0 &&
                HdrGetBitrateIdx(d, p) != 15 &&
                HdrGetSampleRateIdx(d, p) != 3;
        }

        /// <summary>比较两个帧头 (忽略不影响帧布局的位)。</summary>
        private static bool HdrCompareAt(byte[] d, int p1, int p2)
        {
            return HdrValidAt(d, p2) &&
                ((d[p1 + 1] ^ d[p2 + 1]) & 0xFE) == 0 &&
                ((d[p1 + 2] ^ d[p2 + 2]) & 0x0C) == 0 &&
                HdrIsFreeFormat(d, p1) == HdrIsFreeFormat(d, p2);
        }

        /// <summary>比较缓存的 4 字节头与流中帧头。</summary>
        private static bool HdrCompareCached(byte[] h4, byte[] d, int p)
        {
            return HdrValidAt(d, p) &&
                ((h4[1] ^ d[p + 1]) & 0xFE) == 0 &&
                ((h4[2] ^ d[p + 2]) & 0x0C) == 0 &&
                HdrIsFreeFormat(h4, 0) == HdrIsFreeFormat(d, p);
        }

        private static int HdrBitrateKbps(byte[] d, int p)
        {
            int mpeg1 = HdrTestMpeg1(d, p) ? 1 : 0;
            int layer = HdrGetLayer(d, p) - 1;
            int idx = HdrGetBitrateIdx(d, p);
            return 2 * Mp3Tables.HalfRate[mpeg1 * 45 + layer * 15 + idx];
        }

        private static int HdrSampleRateHz(byte[] d, int p)
        {
            int sr = GHertz[HdrGetSampleRateIdx(d, p)];
            if (!HdrTestMpeg1(d, p)) sr >>= 1;
            if (!HdrTestNotMpeg25(d, p)) sr >>= 1;
            return sr;
        }

        private static int HdrFrameSamples(byte[] d, int p) =>
            HdrIsLayer1(d, p) ? 384 : (1152 >> (HdrIsFrame576(d, p) ? 1 : 0));

        private static int HdrFrameBytes(byte[] d, int p, int freeFormatSize)
        {
            int frameBytes = HdrFrameSamples(d, p) * HdrBitrateKbps(d, p) * 125 / HdrSampleRateHz(d, p);
            if (HdrIsLayer1(d, p)) frameBytes &= ~3; // slot 对齐
            return frameBytes != 0 ? frameBytes : freeFormatSize;
        }

        private static int HdrPadding(byte[] d, int p) =>
            HdrTestPadding(d, p) ? (HdrIsLayer1(d, p) ? 4 : 1) : 0;

        // ====================================================================
        // 位流 (对应 minimp3 bs_t)
        // ====================================================================

        private struct Bs
        {
            public byte[] Buf;
            public int BufOffset;
            public int Pos;    // 位
            public int Limit;  // 位

            public void Init(byte[] buf, int offset, int bytes)
            {
                Buf = buf; BufOffset = offset; Pos = 0; Limit = bytes * 8;
            }
        }

        private static uint GetBits(ref Bs bs, int n)
        {
            uint cache = 0;
            int s = bs.Pos & 7;
            int shl = n + s;
            int p = bs.BufOffset + (bs.Pos >> 3);
            bs.Pos += n;
            if (bs.Pos > bs.Limit) return 0;
            uint next = bs.Buf[p++] & (uint)(255 >> s);
            while ((shl -= 8) > 0)
            {
                cache |= next << shl;
                next = bs.Buf[p++];
            }
            return cache | (next >> -shl);
        }

        // ====================================================================
        // 公开 API
        // ====================================================================

        /// <summary>
        /// 从字节数组加载MP3并解析元数据 (帧头/ID3标签/帧统计)。
        /// </summary>
        public Mp3Info Load(byte[] data)
        {
            _fileData = data ?? throw new ArgumentNullException(nameof(data));
            _info = new Mp3Info();
            _frameOffset = 0;
            Reset();

            // 查找ID3v2标签
            if (data.Length > 10 && data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
            {
                _frameOffset = ParseID3v2(data);
            }

            // 定位第一帧并解析头部
            int pos = FindFirstFrame(data, _frameOffset);
            if (pos >= 0)
            {
                FillInfoFromHeader(data, pos, _info);
                _frameOffset = pos;
            }

            CountFrames(data);
            return _info;
        }

        public Mp3Info Load(string path) => Load(File.ReadAllBytes(path));

        /// <summary>
        /// 解码全部样本为 float[-1,1] (声道交错)。
        /// </summary>
        /// <summary>
        /// 解析首帧 Xing/Info + LAME 的 gapless 信息。
        /// 返回 (是否元数据帧, 编码延迟, 尾部填充)。CBR/VBR 编码的标准做法：
        /// 参考解码器(mpg123/libsndfile)按 enc_delay / enc_padding 裁剪输出。
        /// </summary>
        private (bool metaFrame, int encDelay, int encPadding) ParseGapless()
        {
            int pos = _frameOffset;
            if (_fileData == null || pos < 0 || pos + 4 > _fileData.Length) return (false, 0, 0);
            int frameSize = HdrFrameBytes(_fileData, pos, _freeFormatBytes) + HdrPadding(_fileData, pos);
            int end = Math.Min(_fileData.Length, pos + Math.Max(frameSize, 4));
            int k = -1;
            for (int i = pos + 4; i + 4 <= end; i++)
            {
                bool xing = _fileData[i] == (byte)'X' && _fileData[i + 1] == (byte)'i'
                         && _fileData[i + 2] == (byte)'n' && _fileData[i + 3] == (byte)'g';
                bool info = _fileData[i] == (byte)'I' && _fileData[i + 1] == (byte)'n'
                         && _fileData[i + 2] == (byte)'f' && _fileData[i + 3] == (byte)'o';
                if (xing || info) { k = i; break; }
            }
            if (k < 0) return (false, 0, 0);
            int p = k + 4;
            if (p + 4 > end) return (true, 0, 0);
            uint flags = (uint)((_fileData[p] << 24) | (_fileData[p + 1] << 16)
                              | (_fileData[p + 2] << 8) | _fileData[p + 3]);
            int q = p + 4;
            if ((flags & 1) != 0) q += 4;   // frame count
            if ((flags & 2) != 0) q += 4;   // byte count
            if ((flags & 4) != 0) q += 100; // TOC
            if ((flags & 8) != 0) q += 4;   // quality
            int encDelay = 0, encPadding = 0;
            if (q + 24 <= end && _fileData[q] == (byte)'L' && _fileData[q + 1] == (byte)'A'
             && _fileData[q + 2] == (byte)'M' && _fileData[q + 3] == (byte)'E')
            {
                int b0 = _fileData[q + 21], b1 = _fileData[q + 22], b2 = _fileData[q + 23];
                encDelay = (b0 << 4) | (b1 >> 4);
                encPadding = ((b1 & 0x0F) << 8) | b2;
            }
            return (true, encDelay, encPadding);
        }

        public float[] DecodeAll()
        {
            if (_fileData == null || _info == null)
                throw new InvalidOperationException("未加载MP3文件");

            Reset();
            var output = new List<float>(1 << 16);
            var pcm = new float[1152 * 2];
            var info = new FrameInfo();
            int pos = _frameOffset;
            int total = _fileData.Length;
            var gap = ParseGapless();
            bool first = true;

            while (pos < total)
            {
                int samples = DecodeFrame(_fileData, pos, total - pos, pcm, info);
                if (info.FrameBytes <= 0) break;
                // Xing/Info 帧只含元数据，不计入 PCM 输出（Xing 的 frame count 亦不含它）。
                if (samples > 0 && !(gap.metaFrame && first))
                {
                    int n = samples * info.Channels;
                    for (int i = 0; i < n; i++) output.Add(pcm[i]);
                }
                first = false;
                pos += info.FrameBytes;
            }

            // Gapless 裁剪：跳过编码延迟、丢弃尾部填充，与 mpg123/libsndfile 口径一致。
            if (gap.encDelay > 0 || gap.encPadding > 0)
            {
                int skip = Math.Min(gap.encDelay, output.Count);
                int keep = output.Count - skip - gap.encPadding;
                if (keep <= 0) return output.ToArray();
                var trimmed = new float[keep];
                output.CopyTo(skip, trimmed, 0, keep);
                return trimmed;
            }
            return output.ToArray();
        }

        public void Reset()
        {
            _header[0] = 0;
            _freeFormatBytes = 0;
            _reserv = 0;
            Array.Clear(_qmfState, 0, _qmfState.Length);
            Array.Clear(_mdctOverlap[0], 0, _mdctOverlap[0].Length);
            Array.Clear(_mdctOverlap[1], 0, _mdctOverlap[1].Length);
        }

        // ====================================================================
        // ID3 与元数据
        // ====================================================================

        /// <summary>
        /// 解析ID3v2标签, 返回标签结束偏移。
        /// </summary>
        private int ParseID3v2(byte[] data)
        {
            int majorVersion = data[3];
            int size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
            int tagEnd = Math.Min(10 + size, data.Length);

            int pos = 10;
            while (pos < tagEnd - 10)
            {
                string frameId = Encoding.ASCII.GetString(data, pos, 4);
                int frameSize = (majorVersion == 4)
                    ? (data[pos + 4] << 21) | (data[pos + 5] << 14) | (data[pos + 6] << 7) | data[pos + 7]
                    : (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];

                if (frameSize <= 0 || pos + 10 + frameSize > tagEnd) break;

                if (frameSize > 0 && pos + 11 <= data.Length)
                {
                    byte encoding = data[pos + 10];
                    string text = "";
                    try
                    {
                        if (encoding == 0)
                            text = Encoding.GetEncoding("ISO-8859-1").GetString(data, pos + 11, frameSize - 1);
                        else if (encoding == 1)
                            text = Encoding.Unicode.GetString(data, pos + 11, frameSize - 1);
                        else if (encoding == 3)
                            text = Encoding.UTF8.GetString(data, pos + 11, frameSize - 1);
                    }
                    catch { }

                    text = text.TrimEnd('\0', ' ');
                    switch (frameId)
                    {
                        case "TIT2": _info.Title = text; break;
                        case "TPE1": _info.Artist = text; break;
                        case "TALB": _info.Album = text; break;
                        case "TRCK":
                            if (int.TryParse(text.Split('/')[0], out int track)) _info.Track = track;
                            break;
                    }
                }
                pos += 10 + frameSize;
            }

            // ID3v1 (文件末尾128字节)
            if (_fileData.Length > 128)
            {
                int v1Offset = _fileData.Length - 128;
                if (_fileData[v1Offset] == 0x54 && _fileData[v1Offset + 1] == 0x41 && _fileData[v1Offset + 2] == 0x47)
                {
                    if (string.IsNullOrEmpty(_info.Title))
                        _info.Title = Encoding.GetEncoding("ISO-8859-1").GetString(_fileData, v1Offset + 3, 30).TrimEnd('\0', ' ');
                    if (string.IsNullOrEmpty(_info.Artist))
                        _info.Artist = Encoding.GetEncoding("ISO-8859-1").GetString(_fileData, v1Offset + 33, 30).TrimEnd('\0', ' ');
                    if (string.IsNullOrEmpty(_info.Album))
                        _info.Album = Encoding.GetEncoding("ISO-8859-1").GetString(_fileData, v1Offset + 63, 30).TrimEnd('\0', ' ');
                }
            }
            return tagEnd;
        }

        private void FillInfoFromHeader(byte[] data, int pos, Mp3Info info)
        {
            int versionBits = (data[pos + 1] >> 3) & 0x03;
            info.Version = versionBits switch { 0 => MpegVersion.Mpeg25, 2 => MpegVersion.Mpeg2, _ => MpegVersion.Mpeg1 };
            int layerBits = (data[pos + 1] >> 1) & 0x03;
            info.Layer = layerBits switch { 1 => MpegLayer.Layer3, 2 => MpegLayer.Layer2, 3 => MpegLayer.Layer1, _ => MpegLayer.Layer3 };
            info.Bitrate = HdrBitrateKbps(data, pos) * 1000;
            info.SampleRate = HdrSampleRateHz(data, pos);
            info.Channels = ((data[pos + 3] >> 6) & 0x03) == 3 ? 1 : 2;
            info.SamplesPerFrame = HdrFrameSamples(data, pos);
            info.FrameSize = HdrFrameBytes(data, pos, 0) + HdrPadding(data, pos);
        }

        /// <summary>
        /// 查找第一个有效帧头位置 (要求后续帧头一致以避免误同步)。
        /// </summary>
        private static int FindFirstFrame(byte[] data, int start)
        {
            for (int i = start; i < data.Length - HdrSize; i++)
            {
                if (data[i] == 0xFF && HdrValidAt(data, i))
                {
                    int step = HdrFrameBytes(data, i, 0) + HdrPadding(data, i);
                    if (step > 0 && i + step + HdrSize < data.Length && HdrValidAt(data, i + step))
                        return i;
                    if (step > 0 && i + step >= data.Length) return i; // 单帧文件
                }
            }
            return -1;
        }

        private void CountFrames(byte[] data)
        {
            long frameCount = 0;
            int pos = _frameOffset;
            while (pos < data.Length - HdrSize)
            {
                if (data[pos] == 0xFF && HdrValidAt(data, pos))
                {
                    int frameBytes = HdrFrameBytes(data, pos, 0);
                    if (frameBytes <= 0) break;
                    frameCount++;
                    pos += frameBytes + HdrPadding(data, pos);
                }
                else
                {
                    pos++;
                }
            }
            _info.NumFrames = frameCount;
            _info.NumSamples = frameCount * _info.SamplesPerFrame;
            _info.DurationSeconds = _info.SampleRate > 0
                ? (double)_info.NumSamples / _info.SampleRate
                : 0;
        }

        // ====================================================================
        // 帧定位 (对应 mp3d_find_frame / mp3d_match_frame)
        // ====================================================================

        private static bool MatchFrame(byte[] mp3, int baseOff, int mp3Bytes, int frameBytes)
        {
            int i = 0;
            for (int nmatch = 0; nmatch < MaxFrameSyncMatches; nmatch++)
            {
                i += HdrFrameBytes(mp3, baseOff + i, frameBytes) + HdrPadding(mp3, baseOff + i);
                if (i + HdrSize > mp3Bytes) return nmatch > 0;
                if (!HdrCompareAt(mp3, baseOff, baseOff + i)) return false;
            }
            return true;
        }

        private static int FindFrame(byte[] mp3, int mp3Offset, int mp3Bytes, ref int freeFormatBytes, out int frameBytesOut)
        {
            frameBytesOut = 0;
            for (int i = 0; i < mp3Bytes - HdrSize; i++)
            {
                int baseOff = mp3Offset + i;
                if (mp3[baseOff] != 0xFF || !HdrValidAt(mp3, baseOff)) continue;

                int frameBytes = HdrFrameBytes(mp3, baseOff, freeFormatBytes);
                int frameAndPadding = frameBytes + HdrPadding(mp3, baseOff);

                for (int k = HdrSize; frameBytes == 0 && k < MaxFreeFormatFrameSize && i + 2 * k < mp3Bytes - HdrSize; k++)
                {
                    if (HdrCompareAt(mp3, baseOff, baseOff + k))
                    {
                        int fb = k - HdrPadding(mp3, baseOff);
                        int nextfb = fb + HdrPadding(mp3, baseOff + k);
                        if (i + k + nextfb + HdrSize > mp3Bytes || !HdrCompareAt(mp3, baseOff, baseOff + k + nextfb))
                            continue;
                        frameAndPadding = k;
                        frameBytes = fb;
                        freeFormatBytes = fb;
                        break;
                    }
                }

                if ((frameBytes != 0 && i + frameAndPadding <= mp3Bytes &&
                     MatchFrame(mp3, baseOff, mp3Bytes - i, frameBytes)) ||
                    (i == 0 && frameAndPadding == mp3Bytes))
                {
                    frameBytesOut = frameAndPadding;
                    return i;
                }
                freeFormatBytes = 0;
            }
            frameBytesOut = 0;
            return mp3Bytes;
        }

        // ====================================================================
        // Layer III 边信息
        // ====================================================================

        private sealed class GrInfo
        {
            public byte[] SfbTab;
            public int SfbTabOffset;
            public int Part23Length, BigValues, ScalefacCompress;
            public int GlobalGain, BlockType, MixedBlockFlag, NLongSfb, NShortSfb;
            public readonly int[] TableSelect = new int[3];
            public readonly int[] RegionCount = new int[3];
            public readonly int[] SubblockGain = new int[3];
            public int Preflag, ScalefacScale, Count1Table, Scfsi;
        }

        private int L3ReadSideInfo(ref Bs bs, GrInfo[] gr, int grOffset, byte[] hdr)
        {
            int tables, scfsi = 0;
            int mainDataBegin, part23Sum = 0;
            int srIdx = HdrGetMySampleRateIdx(hdr, 0); srIdx -= srIdx != 0 ? 1 : 0;
            int grCount = HdrIsMono(hdr, 0) ? 1 : 2;
            bool mpeg1 = HdrTestMpeg1(hdr, 0);

            if (mpeg1)
            {
                grCount *= 2;
                mainDataBegin = (int)GetBits(ref bs, 9);
                scfsi = (int)GetBits(ref bs, 7 + grCount);
            }
            else
            {
                mainDataBegin = (int)GetBits(ref bs, 8 + grCount) >> grCount;
            }

            int gi = grOffset;
            do
            {
                if (HdrIsMono(hdr, 0)) scfsi <<= 4;
                var g = gr[gi];
                g.Part23Length = (int)GetBits(ref bs, 12);
                part23Sum += g.Part23Length;
                g.BigValues = (int)GetBits(ref bs, 9);
                if (g.BigValues > 288) return -1;
                g.GlobalGain = (int)GetBits(ref bs, 8);
                g.ScalefacCompress = (int)GetBits(ref bs, mpeg1 ? 4 : 9);
                g.SfbTab = Mp3Tables.ScfLong; g.SfbTabOffset = srIdx * 23;
                g.NLongSfb = 22; g.NShortSfb = 0;
                if (GetBits(ref bs, 1) != 0)
                {
                    g.BlockType = (int)GetBits(ref bs, 2);
                    if (g.BlockType == 0) return -1;
                    g.MixedBlockFlag = (int)GetBits(ref bs, 1);
                    g.RegionCount[0] = 7;
                    g.RegionCount[1] = 255;
                    if (g.BlockType == ShortBlockType)
                    {
                        scfsi &= 0x0F0F;
                        if (g.MixedBlockFlag == 0)
                        {
                            g.RegionCount[0] = 8;
                            g.SfbTab = Mp3Tables.ScfShort; g.SfbTabOffset = srIdx * 40;
                            g.NLongSfb = 0; g.NShortSfb = 39;
                        }
                        else
                        {
                            g.SfbTab = Mp3Tables.ScfMixed; g.SfbTabOffset = srIdx * 40;
                            g.NLongSfb = mpeg1 ? 8 : 6;
                            g.NShortSfb = 30;
                        }
                    }
                    tables = (int)GetBits(ref bs, 10);
                    tables <<= 5;
                    g.SubblockGain[0] = (int)GetBits(ref bs, 3);
                    g.SubblockGain[1] = (int)GetBits(ref bs, 3);
                    g.SubblockGain[2] = (int)GetBits(ref bs, 3);
                }
                else
                {
                    g.BlockType = 0;
                    g.MixedBlockFlag = 0;
                    tables = (int)GetBits(ref bs, 15);
                    g.RegionCount[0] = (int)GetBits(ref bs, 4);
                    g.RegionCount[1] = (int)GetBits(ref bs, 3);
                    g.RegionCount[2] = 255;
                }
                g.TableSelect[0] = tables >> 10;
                g.TableSelect[1] = (tables >> 5) & 31;
                g.TableSelect[2] = tables & 31;
                g.Preflag = mpeg1 ? (int)GetBits(ref bs, 1) : (g.ScalefacCompress >= 500 ? 1 : 0);
                g.ScalefacScale = (int)GetBits(ref bs, 1);
                g.Count1Table = (int)GetBits(ref bs, 1);
                g.Scfsi = (scfsi >> 12) & 15;
                scfsi <<= 4;
                gi++;
            } while (--grCount > 0);

            if (part23Sum + bs.Pos > bs.Limit + mainDataBegin * 8) return -1;
            return mainDataBegin;
        }

        // ====================================================================
        // Layer III 缩放因子
        // ====================================================================

        private static float L3LdexpQ2(float y, int expQ2)
        {
            float[] expfrac = { 9.31322575e-10f, 7.83145814e-10f, 6.58544508e-10f, 5.53767716e-10f };
            int e;
            do
            {
                e = Math.Min(30 * 4, expQ2);
                y *= expfrac[e & 3] * ((1 << 30) >> (e >> 2));
            } while ((expQ2 -= e) > 0);
            return y;
        }

        private void L3ReadScalefactors(byte[] scf, int scfOff, byte[] istPos, int istOff,
                                        byte[] scfSize, byte[] scfCount, int scfCountOff, ref Bs bs, int scfsi)
        {
            int i, k;
            int scfIdx = scfOff, istIdx = istOff;
            for (i = 0; i < 4 && scfCount[scfCountOff + i] != 0; i++, scfsi *= 2)
            {
                int cnt = scfCount[scfCountOff + i];
                if ((scfsi & 8) != 0)
                {
                    Array.Copy(istPos, istIdx, scf, scfIdx, cnt);
                }
                else
                {
                    int bits = scfSize[i];
                    if (bits == 0)
                    {
                        Array.Clear(scf, scfIdx, cnt);
                        Array.Clear(istPos, istIdx, cnt);
                    }
                    else
                    {
                        int maxScf = (scfsi < 0) ? (1 << bits) - 1 : -1;
                        for (k = 0; k < cnt; k++)
                        {
                            int s = (int)GetBits(ref bs, bits);
                            istPos[istIdx + k] = (byte)(s == maxScf ? -1 : s);
                            scf[scfIdx + k] = (byte)s;
                        }
                    }
                }
                istIdx += cnt;
                scfIdx += cnt;
            }
            scf[scfIdx] = scf[scfIdx + 1] = scf[scfIdx + 2] = 0;
        }

        private void L3DecodeScalefactors(byte[] hdr, byte[] istPos, int istOff, ref Bs bs,
                                          GrInfo gr, float[] scf, int ch)
        {
            int partIdx = (gr.NShortSfb != 0 ? 1 : 0) + (gr.NLongSfb == 0 ? 1 : 0);
            int partOff = partIdx * 28;
            var scfSize = new byte[4];
            var iscf = new byte[40];
            int scfShift = gr.ScalefacScale + 1;
            int scfsi = gr.Scfsi;

            if (HdrTestMpeg1(hdr, 0))
            {
                int[] scfcDecode = { 0, 1, 2, 3, 12, 5, 6, 7, 9, 10, 11, 13, 14, 15, 18, 19 };
                int part = scfcDecode[gr.ScalefacCompress];
                scfSize[1] = scfSize[0] = (byte)(part >> 2);
                scfSize[3] = scfSize[2] = (byte)(part & 3);
            }
            else
            {
                int[] gmod = { 5, 5, 4, 4, 5, 5, 4, 1, 4, 3, 1, 1, 5, 6, 6, 1, 4, 4, 4, 1, 4, 3, 1, 1 };
                int modprod, sfc, ist = HdrTestIStereo(hdr, 0) && ch != 0 ? 1 : 0;
                sfc = gr.ScalefacCompress >> ist;
                int k;
                for (k = ist * 3 * 4; sfc >= 0; sfc -= modprod, k += 4)
                {
                    modprod = 1;
                    for (int i = 3; i >= 0; i--)
                    {
                        scfSize[i] = (byte)(sfc / modprod % gmod[k + i]);
                        modprod *= gmod[k + i];
                    }
                }
                partOff += k;
                scfsi = -16;
            }
            L3ReadScalefactors(iscf, 0, istPos, istOff, scfSize, Mp3Tables.ScfPartitions, partOff, ref bs, scfsi);

            if (gr.NShortSfb != 0)
            {
                int sh = 3 - scfShift;
                for (int i = 0; i < gr.NShortSfb; i += 3)
                {
                    iscf[gr.NLongSfb + i + 0] += (byte)(gr.SubblockGain[0] << sh);
                    iscf[gr.NLongSfb + i + 1] += (byte)(gr.SubblockGain[1] << sh);
                    iscf[gr.NLongSfb + i + 2] += (byte)(gr.SubblockGain[2] << sh);
                }
            }
            else if (gr.Preflag != 0)
            {
                int[] preamp = { 1, 1, 1, 1, 2, 2, 3, 3, 3, 2 };
                for (int i = 0; i < 10; i++)
                    iscf[11 + i] += (byte)preamp[i];
            }

            int gainExp = gr.GlobalGain + BitsDequantizerOut * 4 - 210 - (HdrIsMsStereo(hdr, 0) ? 2 : 0);
            float gain = L3LdexpQ2(1 << (MaxScfi / 4), MaxScfi - gainExp);
            for (int i = 0; i < gr.NLongSfb + gr.NShortSfb; i++)
                scf[i] = L3LdexpQ2(gain, iscf[i] << scfShift);
        }

        // ====================================================================
        // Layer III 重量化 + Huffman
        // ====================================================================

        private static float L3Pow43(int x)
        {
            var pow43 = Mp3Tables.Pow43;
            int mult = 256;
            if (x < 129) return pow43[16 + x];
            if (x < 1024)
            {
                mult = 16;
                x <<= 3;
            }
            int sign = 2 * x & 64;
            float frac = (float)((x & 63) - sign) / ((x & ~63) + sign);
            return pow43[16 + ((x + sign) >> 6)] * (1.0f + frac * ((4.0f / 3) + frac * (2.0f / 9))) * mult;
        }

        private void L3Huffman(float[] dst, int dstOff, ref Bs bs, GrInfo gr, float[] scf, int layer3grLimit)
        {
            var tabs = Mp3Tables.HuffTabs;
            var tabindex = Mp3Tables.HuffTabIndex;
            var tab32 = Mp3Tables.Tab32;
            var tab33 = Mp3Tables.Tab33;
            var linbits = Mp3Tables.LinBits;
            var pow43 = Mp3Tables.Pow43;
            byte[] md = bs.Buf;

            float one = 0.0f;
            int ireg = 0, bigValCnt = gr.BigValues;
            int sfbIdx = gr.SfbTabOffset;
            int scfIdx = 0;
            int dstIdx = dstOff;

            int bsNextPtr = bs.BufOffset + (bs.Pos >> 3);
            uint bsCache = (uint)(((md[bsNextPtr] * 256 + md[bsNextPtr + 1]) * 256 + md[bsNextPtr + 2]) * 256 + md[bsNextPtr + 3]) << (bs.Pos & 7);
            int bsSh = (bs.Pos & 7) - 8;
            bsNextPtr += 4;

            while (bigValCnt > 0)
            {
                int tabNum = gr.TableSelect[ireg];
                int sfbCnt = gr.RegionCount[ireg];
                ireg++;
                int cbOff = tabindex[tabNum];
                int lb = linbits[tabNum];
                if (lb != 0)
                {
                    int np;
                    do
                    {
                        np = gr.SfbTab[sfbIdx++] / 2;
                        int pairsToDecode = Math.Min(bigValCnt, np);
                        one = scf[scfIdx++];
                        do
                        {
                            int w = 5;
                            int leaf = tabs[cbOff + (int)(bsCache >> (32 - w))];
                            while (leaf < 0)
                            {
                                bsCache <<= w; bsSh += w;
                                w = leaf & 7;
                                leaf = tabs[cbOff + (int)(bsCache >> (32 - w)) - (leaf >> 3)];
                            }
                            bsCache <<= (leaf >> 8); bsSh += (leaf >> 8);

                            for (int j = 0; j < 2; j++, dstIdx++, leaf >>= 4)
                            {
                                int lsb = leaf & 0x0F;
                                if (lsb == 15)
                                {
                                    lsb += (int)(bsCache >> (32 - lb));
                                    bsCache <<= lb; bsSh += lb;
                                    while (bsSh >= 0) { bsCache |= (uint)md[bsNextPtr++] << bsSh; bsSh -= 8; }
                                    dst[dstIdx] = one * L3Pow43(lsb) * ((int)bsCache < 0 ? -1 : 1);
                                }
                                else
                                {
                                    dst[dstIdx] = pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                                }
                                if (lsb != 0) { bsCache <<= 1; bsSh += 1; }
                            }
                            while (bsSh >= 0) { bsCache |= (uint)md[bsNextPtr++] << bsSh; bsSh -= 8; }
                        } while (--pairsToDecode > 0);
                    } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
                }
                else
                {
                    int np;
                    do
                    {
                        np = gr.SfbTab[sfbIdx++] / 2;
                        int pairsToDecode = Math.Min(bigValCnt, np);
                        one = scf[scfIdx++];
                        do
                        {
                            int w = 5;
                            int leaf = tabs[cbOff + (int)(bsCache >> (32 - w))];
                            while (leaf < 0)
                            {
                                bsCache <<= w; bsSh += w;
                                w = leaf & 7;
                                leaf = tabs[cbOff + (int)(bsCache >> (32 - w)) - (leaf >> 3)];
                            }
                            bsCache <<= (leaf >> 8); bsSh += (leaf >> 8);

                            for (int j = 0; j < 2; j++, dstIdx++, leaf >>= 4)
                            {
                                int lsb = leaf & 0x0F;
                                dst[dstIdx] = pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                                if (lsb != 0) { bsCache <<= 1; bsSh += 1; }
                            }
                            while (bsSh >= 0) { bsCache |= (uint)md[bsNextPtr++] << bsSh; bsSh -= 8; }
                        } while (--pairsToDecode > 0);
                    } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
                }
            }

            // count1 区域 (四元组, 值为 ±one)
            int np2 = 1 - bigValCnt;
            for (; ; dstIdx += 4)
            {
                var cb = gr.Count1Table != 0 ? tab33 : tab32;
                int leaf = cb[bsCache >> 28];
                if ((leaf & 8) == 0)
                {
                    int sh = 32 - (leaf & 3);
                    int extra = sh >= 32 ? 0 : (int)((bsCache << 4) >> sh);
                    int idx = (leaf >> 3) + extra;
                    if (idx >= cb.Length) throw new InvalidDataException("MP3 count1 Huffman越界");
                    leaf = cb[idx];
                }
                bsCache <<= (leaf & 7); bsSh += (leaf & 7);
                int bsPos = (bsNextPtr - bs.BufOffset) * 8 - 24 + bsSh;
                if (bsPos > layer3grLimit) break;

                if (--np2 == 0)
                {
                    np2 = gr.SfbTab[sfbIdx++] / 2;
                    if (np2 == 0) break;
                    one = scf[scfIdx++];
                }
                if ((leaf & 128) != 0) { dst[dstIdx + 0] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
                if ((leaf & 64) != 0) { dst[dstIdx + 1] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
                if (--np2 == 0)
                {
                    np2 = gr.SfbTab[sfbIdx++] / 2;
                    if (np2 == 0) break;
                    one = scf[scfIdx++];
                }
                if ((leaf & 32) != 0) { dst[dstIdx + 2] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
                if ((leaf & 16) != 0) { dst[dstIdx + 3] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
                while (bsSh >= 0) { bsCache |= (uint)md[bsNextPtr++] << bsSh; bsSh -= 8; }
            }

            bs.Pos = layer3grLimit;
        }

        // ====================================================================
        // Layer III 立体声处理
        // ====================================================================

        private static void L3MidsideStereo(float[] buf, int leftOff, int n)
        {
            int rightOff = leftOff + 576;
            for (int i = 0; i < n; i++)
            {
                float a = buf[leftOff + i];
                float b = buf[rightOff + i];
                buf[leftOff + i] = a + b;
                buf[rightOff + i] = a - b;
            }
        }

        private static void L3IntensityStereoBand(float[] buf, int leftOff, int n, float kl, float kr)
        {
            int rightOff = leftOff + 576;
            for (int i = 0; i < n; i++)
            {
                buf[rightOff + i] = buf[leftOff + i] * kr;
                buf[leftOff + i] *= kl;
            }
        }

        private static void L3StereoTopBand(float[] right, int rightOff, byte[] sfb, int sfbOff, int nbands, int[] maxBand)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = -1;
            int off = rightOff;
            for (int i = 0; i < nbands; i++)
            {
                int width = sfb[sfbOff + i];
                for (int k = 0; k < width; k += 2)
                {
                    if (right[off + k] != 0 || right[off + k + 1] != 0)
                    {
                        maxBand[i % 3] = i;
                        break;
                    }
                }
                off += width;
            }
        }

        private static void L3StereoProcess(float[] left, int leftOff, byte[] istPos, int istOff,
                                            byte[] sfb, int sfbOff, byte[] hdr, int[] maxBand, int mpeg2Sh)
        {
            var pan = Mp3Tables.Pan;
            bool mpeg1 = HdrTestMpeg1(hdr, 0);
            int maxPos = mpeg1 ? 7 : 64;
            int off = leftOff, sIdx = sfbOff;

            for (int i = 0; sfb[sIdx] != 0; i++)
            {
                int width = sfb[sIdx];
                int ipos = istPos[istOff + i];
                if (i > maxBand[i % 3] && ipos < maxPos)
                {
                    float kl, kr, s = HdrTestMsStereo(hdr, 0) ? 1.41421356f : 1;
                    if (mpeg1)
                    {
                        kl = pan[2 * ipos];
                        kr = pan[2 * ipos + 1];
                    }
                    else
                    {
                        kl = 1;
                        kr = L3LdexpQ2(1, (ipos + 1) >> 1 << mpeg2Sh);
                        if ((ipos & 1) != 0)
                        {
                            kl = kr;
                            kr = 1;
                        }
                    }
                    L3IntensityStereoBand(left, off, width, kl * s, kr * s);
                }
                else if (HdrTestMsStereo(hdr, 0))
                {
                    L3MidsideStereo(left, off, width);
                }
                off += width;
                sIdx++;
            }
        }

        private void L3IntensityStereo(float[] grbuf, int grOff, byte[] istPos, int istOff, GrInfo gr, byte[] hdr)
        {
            int[] maxBand = new int[3];
            int nSfb = gr.NLongSfb + gr.NShortSfb;
            int maxBlocks = gr.NShortSfb != 0 ? 3 : 1;

            L3StereoTopBand(grbuf, grOff + 576, gr.SfbTab, gr.SfbTabOffset, nSfb, maxBand);
            if (gr.NLongSfb != 0)
            {
                int m = Math.Max(maxBand[0], Math.Max(maxBand[1], maxBand[2]));
                maxBand[0] = maxBand[1] = maxBand[2] = m;
            }
            for (int i = 0; i < maxBlocks; i++)
            {
                int defaultPos = HdrTestMpeg1(hdr, 0) ? 3 : 0;
                int itop = nSfb - maxBlocks + i;
                int prev = itop - maxBlocks;
                istPos[istOff + itop] = maxBand[i] >= prev ? (byte)defaultPos : istPos[istOff + prev];
            }
            L3StereoProcess(grbuf, grOff, istPos, istOff, gr.SfbTab, gr.SfbTabOffset, hdr, maxBand, gr.ScalefacCompress & 1);
        }

        // ====================================================================
        // Layer III 重排序 / 抗混叠 / IMDCT
        // ====================================================================

        private static void L3Reorder(float[] grbuf, int grOff, float[] scratch, byte[] sfb, int sfbOff)
        {
            int src = grOff, dst = 0, sIdx = sfbOff;
            int len;
            while ((len = sfb[sIdx]) != 0)
            {
                sIdx += 3;
                for (int i = 0; i < len; i++, src++)
                {
                    scratch[dst++] = grbuf[src];
                    scratch[dst++] = grbuf[src + len];
                    scratch[dst++] = grbuf[src + 2 * len];
                }
                src += len;
            }
            Array.Copy(scratch, 0, grbuf, grOff, dst);
        }

        private static readonly float[] GAa0 = { 0.85749293f, 0.88174200f, 0.94962865f, 0.98331459f, 0.99551782f, 0.99916056f, 0.99989920f, 0.99999316f };
        private static readonly float[] GAa1 = { 0.51449576f, 0.47173197f, 0.31337745f, 0.18191320f, 0.09457419f, 0.04096558f, 0.01419856f, 0.00369997f };

        private static void L3Antialias(float[] grbuf, int grOff, int nbands)
        {
            int off = grOff;
            for (; nbands > 0; nbands--, off += 18)
            {
                for (int i = 0; i < 8; i++)
                {
                    float u = grbuf[off + 18 + i];
                    float d = grbuf[off + 17 - i];
                    grbuf[off + 18 + i] = u * GAa0[i] - d * GAa1[i];
                    grbuf[off + 17 - i] = u * GAa1[i] + d * GAa0[i];
                }
            }
        }

        private static void L3Dct3_9(float[] y, int o)
        {
            float s0, s1, s2, s3, s4, s5, s6, s7, s8, t0, t2, t4;

            s0 = y[o]; s2 = y[o + 2]; s4 = y[o + 4]; s6 = y[o + 6]; s8 = y[o + 8];
            t0 = s0 + s6 * 0.5f;
            s0 -= s6;
            t4 = (s4 + s2) * 0.93969262f;
            t2 = (s8 + s2) * 0.76604444f;
            s6 = (s4 - s8) * 0.17364818f;
            s4 += s8 - s2;

            s2 = s0 - s4 * 0.5f;
            y[o + 4] = s4 + s0;
            s8 = t0 - t2 + s6;
            s0 = t0 - t4 + t2;
            s4 = t0 + t4 - s6;

            s1 = y[o + 1]; s3 = y[o + 3]; s5 = y[o + 5]; s7 = y[o + 7];

            s3 *= 0.86602540f;
            t0 = (s5 + s1) * 0.98480775f;
            t4 = (s5 - s7) * 0.34202014f;
            t2 = (s1 + s7) * 0.64278761f;
            s1 = (s1 - s5 - s7) * 0.86602540f;

            s5 = t0 - s3 - t2;
            s7 = t4 - s3 - t0;
            s3 = t4 + s3 - t2;

            y[o] = s4 - s7;
            y[o + 1] = s2 + s1;
            y[o + 2] = s0 - s3;
            y[o + 3] = s8 + s5;
            y[o + 5] = s8 - s5;
            y[o + 6] = s0 + s3;
            y[o + 7] = s2 - s1;
            y[o + 8] = s4 + s7;
        }

        private static readonly float[] GTwid9 = { 0.73727734f, 0.79335334f, 0.84339145f, 0.88701083f, 0.92387953f, 0.95371695f, 0.97629601f, 0.99144486f, 0.99904822f, 0.67559021f, 0.60876143f, 0.53729961f, 0.46174861f, 0.38268343f, 0.30070580f, 0.21643961f, 0.13052619f, 0.04361938f };

        private static void L3Imdct36(float[] grbuf, int grOff, float[] overlap, int ovlOff, float[] window, int nbands)
        {
            int g = grOff, o = ovlOff;
            for (int j = 0; j < nbands; j++, g += 18, o += 9)
            {
                float[] co = new float[9], si = new float[9];
                co[0] = -grbuf[g];
                si[0] = grbuf[g + 17];
                for (int i = 0; i < 4; i++)
                {
                    si[8 - 2 * i] = grbuf[g + 4 * i + 1] - grbuf[g + 4 * i + 2];
                    co[1 + 2 * i] = grbuf[g + 4 * i + 1] + grbuf[g + 4 * i + 2];
                    si[7 - 2 * i] = grbuf[g + 4 * i + 4] - grbuf[g + 4 * i + 3];
                    co[2 + 2 * i] = -(grbuf[g + 4 * i + 3] + grbuf[g + 4 * i + 4]);
                }
                L3Dct3_9(co, 0);
                L3Dct3_9(si, 0);

                si[1] = -si[1]; si[3] = -si[3]; si[5] = -si[5]; si[7] = -si[7];

                for (int i = 0; i < 9; i++)
                {
                    float ovl = overlap[o + i];
                    float sum = co[i] * GTwid9[9 + i] + si[i] * GTwid9[i];
                    overlap[o + i] = co[i] * GTwid9[i] - si[i] * GTwid9[9 + i];
                    grbuf[g + i] = ovl * window[i] - sum * window[9 + i];
                    grbuf[g + 17 - i] = ovl * window[9 + i] + sum * window[i];
                }
            }
        }

        private static void L3Idct3(float x0, float x1, float x2, float[] dst, int dstOff)
        {
            float m1 = x1 * 0.86602540f;
            float a1 = x0 - x2 * 0.5f;
            dst[dstOff + 1] = x0 + x2;
            dst[dstOff] = a1 + m1;
            dst[dstOff + 2] = a1 - m1;
        }

        private static readonly float[] GTwid3 = { 0.79335334f, 0.92387953f, 0.99144486f, 0.60876143f, 0.38268343f, 0.13052619f };

        private static void L3Imdct12(float[] x, int xOff, float[] dst, int dstOff, float[] overlap, int ovlOff)
        {
            float[] co = new float[3], si = new float[3];

            L3Idct3(-x[xOff], x[xOff + 6] + x[xOff + 3], x[xOff + 12] + x[xOff + 9], co, 0);
            L3Idct3(x[xOff + 15], x[xOff + 12] - x[xOff + 9], x[xOff + 6] - x[xOff + 3], si, 0);
            si[1] = -si[1];

            for (int i = 0; i < 3; i++)
            {
                float ovl = overlap[ovlOff + i];
                float sum = co[i] * GTwid3[3 + i] + si[i] * GTwid3[i];
                overlap[ovlOff + i] = co[i] * GTwid3[i] - si[i] * GTwid3[3 + i];
                dst[dstOff + i] = ovl * GTwid3[2 - i] - sum * GTwid3[5 - i];
                dst[dstOff + 5 - i] = ovl * GTwid3[5 - i] + sum * GTwid3[2 - i];
            }
        }

        private static void L3ImdctShort(float[] grbuf, int grOff, float[] overlap, int ovlOff, int nbands)
        {
            for (; nbands > 0; nbands--, ovlOff += 9, grOff += 18)
            {
                var tmp = new float[18];
                Array.Copy(grbuf, grOff, tmp, 0, 18);
                Array.Copy(overlap, ovlOff, grbuf, grOff, 6);
                L3Imdct12(tmp, 0, grbuf, grOff + 6, overlap, ovlOff + 6);
                L3Imdct12(tmp, 1, grbuf, grOff + 12, overlap, ovlOff + 6);
                L3Imdct12(tmp, 2, overlap, ovlOff, overlap, ovlOff + 6);
            }
        }

        private static void L3ChangeSign(float[] grbuf, int grOff)
        {
            int off = grOff + 18;
            for (int b = 0; b < 32; b += 2, off += 36)
                for (int i = 1; i < 18; i += 2)
                    grbuf[off + i] = -grbuf[off + i];
        }

        private static readonly float[][] GMdctWindow = {
            new float[] { 0.99904822f,0.99144486f,0.97629601f,0.95371695f,0.92387953f,0.88701083f,0.84339145f,0.79335334f,0.73727734f,0.04361938f,0.13052619f,0.21643961f,0.30070580f,0.38268343f,0.46174861f,0.53729961f,0.60876143f,0.67559021f },
            new float[] { 1,1,1,1,1,1,0.99144486f,0.92387953f,0.79335334f,0,0,0,0,0,0,0.13052619f,0.38268343f,0.60876143f }
        };

        private static void L3ImdctGr(float[] grbuf, int grOff, float[] overlap, int ovlOff, int blockType, int nLongBands)
        {
            int g = grOff, o = ovlOff;
            if (nLongBands > 0)
            {
                L3Imdct36(grbuf, g, overlap, o, GMdctWindow[0], nLongBands);
                g += 18 * nLongBands;
                o += 9 * nLongBands;
            }
            if (blockType == ShortBlockType)
                L3ImdctShort(grbuf, g, overlap, o, 32 - nLongBands);
            else
                L3Imdct36(grbuf, g, overlap, o, GMdctWindow[blockType == StopBlockType ? 1 : 0], 32 - nLongBands);
        }

        // ====================================================================
        // Layer III 粒度解码
        // ====================================================================

        private void L3Decode(byte[] hdr, ref Bs bs, GrInfo[] grInfo, int grInfoOff, int nch,
                              float[] grbuf, byte[][] istPos, float[] scf, float[] synScratch)
        {
            for (int ch = 0; ch < nch; ch++)
            {
                int layer3grLimit = bs.Pos + grInfo[grInfoOff + ch].Part23Length;
                L3DecodeScalefactors(hdr, istPos[ch], 0, ref bs, grInfo[grInfoOff + ch], scf, ch);
                L3Huffman(grbuf, ch * 576, ref bs, grInfo[grInfoOff + ch], scf, layer3grLimit);
            }

            if (HdrTestIStereo(hdr, 0))
            {
                L3IntensityStereo(grbuf, 0, istPos[1], 0, grInfo[grInfoOff], hdr);
            }
            else if (HdrIsMsStereo(hdr, 0))
            {
                L3MidsideStereo(grbuf, 0, 576);
            }

            for (int ch = 0; ch < nch; ch++)
            {
                var gr = grInfo[grInfoOff + ch];
                int aaBands = 31;
                int nLongBands = (gr.MixedBlockFlag != 0 ? 2 : 0) << (HdrGetMySampleRateIdx(hdr, 0) == 2 ? 1 : 0);

                if (gr.NShortSfb != 0)
                {
                    aaBands = nLongBands - 1;
                    L3Reorder(grbuf, ch * 576 + nLongBands * 18, synScratch, gr.SfbTab, gr.SfbTabOffset + gr.NLongSfb);
                }

                L3Antialias(grbuf, ch * 576, aaBands);
                L3ImdctGr(grbuf, ch * 576, _mdctOverlap[ch], 0, gr.BlockType, nLongBands);
                L3ChangeSign(grbuf, ch * 576);
            }
        }

        // ====================================================================
        // 多相合成滤波器组 (DCT-II + 窗叠加)
        // ====================================================================

        private static readonly float[] GSec = {
            10.19000816f,0.50060302f,0.50241929f,3.40760851f,0.50547093f,0.52249861f,2.05778098f,0.51544732f,0.56694406f,1.48416460f,0.53104258f,0.64682180f,1.16943991f,0.55310392f,0.78815460f,0.97256821f,0.58293498f,1.06067765f,0.83934963f,0.62250412f,1.72244716f,0.74453628f,0.67480832f,5.10114861f
        };

        private static void Mp3dDctII(float[] grbuf, int grOff, int n)
        {
            var t = new float[4][];
            for (int x = 0; x < 4; x++) t[x] = new float[8];

            for (int k = 0; k < n; k++)
            {
                int yOff = grOff + k;

                for (int i = 0; i < 8; i++)
                {
                    float x0 = grbuf[yOff + i * 18];
                    float x1 = grbuf[yOff + (15 - i) * 18];
                    float x2 = grbuf[yOff + (16 + i) * 18];
                    float x3 = grbuf[yOff + (31 - i) * 18];
                    float t0 = x0 + x3;
                    float t1 = x1 + x2;
                    float t2 = (x1 - x2) * GSec[3 * i + 0];
                    float t3 = (x0 - x3) * GSec[3 * i + 1];
                    t[0][i] = t0 + t1;
                    t[1][i] = (t0 - t1) * GSec[3 * i + 2];
                    t[2][i] = t3 + t2;
                    t[3][i] = (t3 - t2) * GSec[3 * i + 2];
                }
                for (int x8 = 0; x8 < 4; x8++)
                {
                    var x = t[x8];
                    float x0 = x[0], x1 = x[1], x2 = x[2], x3 = x[3], x4 = x[4], x5 = x[5], x6 = x[6], x7 = x[7], xt;
                    xt = x0 - x7; x0 += x7;
                    x7 = x1 - x6; x1 += x6;
                    x6 = x2 - x5; x2 += x5;
                    x5 = x3 - x4; x3 += x4;
                    x4 = x0 - x3; x0 += x3;
                    x3 = x1 - x2; x1 += x2;
                    x[0] = x0 + x1;
                    x[4] = (x0 - x1) * 0.70710677f;
                    x5 = x5 + x6;
                    x6 = (x6 + x7) * 0.70710677f;
                    x7 = x7 + xt;
                    x3 = (x3 + x4) * 0.70710677f;
                    x5 -= x7 * 0.198912367f;  /* rotate by PI/8 */
                    x7 += x5 * 0.382683432f;
                    x5 -= x7 * 0.198912367f;
                    x0 = xt - x6; xt += x6;
                    x[1] = (xt + x7) * 0.50979561f;
                    x[2] = (x4 + x3) * 0.54119611f;
                    x[3] = (x0 - x5) * 0.60134488f;
                    x[5] = (x0 + x5) * 0.89997619f;
                    x[6] = (x4 - x3) * 1.30656302f;
                    x[7] = (xt - x7) * 2.56291556f;
                }
                int y = yOff;
                for (int i = 0; i < 7; i++, y += 4 * 18)
                {
                    grbuf[y] = t[0][i];
                    grbuf[y + 18] = t[2][i] + t[3][i] + t[3][i + 1];
                    grbuf[y + 2 * 18] = t[1][i] + t[1][i + 1];
                    grbuf[y + 3 * 18] = t[2][i + 1] + t[3][i] + t[3][i + 1];
                }
                grbuf[y] = t[0][7];
                grbuf[y + 18] = t[2][7] + t[3][7];
                grbuf[y + 2 * 18] = t[1][7];
                grbuf[y + 3 * 18] = t[3][7];
            }
        }

        private static float ScalePcm(float sample) => sample * (1.0f / 32768.0f);

        private static void Mp3dSynthPair(float[] pcm, int pcmOff, int nch, float[] lins, int zOff)
        {
            float a;
            a = (lins[zOff + 14 * 64] - lins[zOff]) * 29;
            a += (lins[zOff + 1 * 64] + lins[zOff + 13 * 64]) * 213;
            a += (lins[zOff + 12 * 64] - lins[zOff + 2 * 64]) * 459;
            a += (lins[zOff + 3 * 64] + lins[zOff + 11 * 64]) * 2037;
            a += (lins[zOff + 10 * 64] - lins[zOff + 4 * 64]) * 5153;
            a += (lins[zOff + 5 * 64] + lins[zOff + 9 * 64]) * 6574;
            a += (lins[zOff + 8 * 64] - lins[zOff + 6 * 64]) * 37489;
            a += lins[zOff + 7 * 64] * 75038;
            pcm[pcmOff] = ScalePcm(a);

            zOff += 2;
            a = lins[zOff + 14 * 64] * 104;
            a += lins[zOff + 12 * 64] * 1567;
            a += lins[zOff + 10 * 64] * 9727;
            a += lins[zOff + 8 * 64] * 64019;
            a += lins[zOff + 6 * 64] * -9975;
            a += lins[zOff + 4 * 64] * -45;
            a += lins[zOff + 2 * 64] * 146;
            a += lins[zOff] * -5;
            pcm[pcmOff + 16 * nch] = ScalePcm(a);
        }

        private static void Mp3dSynth(float[] xl, int xlOff, float[] dst, int dstOff, int nch, float[] lins, int linsOff)
        {
            var gwin = Mp3Tables.SynthWin;
            int xrOff = xlOff + 576 * (nch - 1);
            int dstlOff = dstOff;
            int dstrOff = dstOff + (nch - 1);

            int zlin = linsOff + 15 * 64;   // zlin 基址 (lins 内相对)
            int wIdx = 0;

            lins[zlin + 4 * 15] = xl[xlOff + 18 * 16];
            lins[zlin + 4 * 15 + 1] = xl[xrOff + 18 * 16];
            lins[zlin + 4 * 15 + 2] = xl[xlOff];
            lins[zlin + 4 * 15 + 3] = xl[xrOff];

            lins[zlin + 4 * 31] = xl[xlOff + 1 + 18 * 16];
            lins[zlin + 4 * 31 + 1] = xl[xrOff + 1 + 18 * 16];
            lins[zlin + 4 * 31 + 2] = xl[xlOff + 1];
            lins[zlin + 4 * 31 + 3] = xl[xrOff + 1];

            Mp3dSynthPair(dst, dstrOff, nch, lins, linsOff + 4 * 15 + 1);
            Mp3dSynthPair(dst, dstrOff + 32 * nch, nch, lins, linsOff + 4 * 15 + 64 + 1);
            Mp3dSynthPair(dst, dstlOff, nch, lins, linsOff + 4 * 15);
            Mp3dSynthPair(dst, dstlOff + 32 * nch, nch, lins, linsOff + 4 * 15 + 64);

            for (int i = 14; i >= 0; i--)
            {
                var a = new float[4];
                var b = new float[4];

                lins[zlin + 4 * i] = xl[xlOff + 18 * (31 - i)];
                lins[zlin + 4 * i + 1] = xl[xrOff + 18 * (31 - i)];
                lins[zlin + 4 * i + 2] = xl[xlOff + 1 + 18 * (31 - i)];
                lins[zlin + 4 * i + 3] = xl[xrOff + 1 + 18 * (31 - i)];
                lins[zlin + 4 * (i + 16)] = xl[xlOff + 1 + 18 * (1 + i)];
                lins[zlin + 4 * (i + 16) + 1] = xl[xrOff + 1 + 18 * (1 + i)];
                lins[zlin + 4 * (i - 16) + 2] = xl[xlOff + 18 * (1 + i)];
                lins[zlin + 4 * (i - 16) + 3] = xl[xrOff + 18 * (1 + i)];

                // S0(0) S2(1) S1(2) S2(3) S1(4) S2(5) S1(6) S2(7)
                // S0: 初始化; S1: a += vz*w0 - vy*w1; S2: a += vy*w1 - vz*w0; b 累加相同
                for (int step = 0; step < 8; step++)
                {
                    int k = step;
                    float w0 = gwin[wIdx++];
                    float w1 = gwin[wIdx++];
                    int vzOff = zlin + 4 * i - k * 64;
                    int vyOff = zlin + 4 * i - (15 - k) * 64;
                    bool first = step == 0;
                    bool flip = (step & 1) == 1; // S2 出现在 step 1,3,5,7
                    for (int j = 0; j < 4; j++)
                    {
                        float vz = lins[vzOff + j];
                        float vy = lins[vyOff + j];
                        if (first)
                        {
                            b[j] = vz * w1 + vy * w0;
                            a[j] = vz * w0 - vy * w1;
                        }
                        else
                        {
                            b[j] += vz * w1 + vy * w0;
                            a[j] += flip ? vy * w1 - vz * w0 : vz * w0 - vy * w1;
                        }
                    }
                }

                dst[dstrOff + (15 - i) * nch] = ScalePcm(a[1]);
                dst[dstrOff + (17 + i) * nch] = ScalePcm(b[1]);
                dst[dstlOff + (15 - i) * nch] = ScalePcm(a[0]);
                dst[dstlOff + (17 + i) * nch] = ScalePcm(b[0]);
                dst[dstrOff + (47 - i) * nch] = ScalePcm(a[3]);
                dst[dstrOff + (49 + i) * nch] = ScalePcm(b[3]);
                dst[dstlOff + (47 - i) * nch] = ScalePcm(a[2]);
                dst[dstlOff + (49 + i) * nch] = ScalePcm(b[2]);
            }
        }

        private void Mp3dSynthGranule(float[] qmfState, float[] grbuf, int grOff, int nbands, int nch, float[] pcm, int pcmOff, float[] lins)
        {
            for (int i = 0; i < nch; i++)
            {
                Mp3dDctII(grbuf, grOff + 576 * i, nbands);
            }

            Array.Copy(qmfState, 0, lins, 0, 15 * 64);
            Array.Clear(lins, 15 * 64, 64 * nbands);

            for (int i = 0; i < nbands; i += 2)
            {
                Mp3dSynth(grbuf, grOff + i, pcm, pcmOff + 32 * nch * i, nch, lins, i * 64);
            }

            if (nch == 1)
            {
                for (int i = 0; i < 15 * 64; i += 2)
                {
                    qmfState[i] = lins[nbands * 64 + i];
                }
            }
            else
            {
                Array.Copy(lins, nbands * 64, qmfState, 0, 15 * 64);
            }
        }

        // ====================================================================
        // 位 reservoir
        // ====================================================================

        private void L3SaveReservoir(ref Bs bs)
        {
            int pos = (bs.Pos + 7) / 8;
            int remains = bs.Limit / 8 - pos;
            if (remains > MaxBitreservoirBytes)
            {
                pos += remains - MaxBitreservoirBytes;
                remains = MaxBitreservoirBytes;
            }
            if (remains > 0)
            {
                Array.Copy(_maindata, pos, _reservBuf, 0, remains);
            }
            _reserv = remains;
        }

        private bool L3RestoreReservoir(ref Bs bs, int mainDataBegin)
        {
            int frameBytes = (bs.Limit - bs.Pos) / 8;
            int bytesHave = Math.Min(_reserv, mainDataBegin);
            Array.Copy(_reservBuf, Math.Max(0, _reserv - mainDataBegin), _maindata, 0, bytesHave);
            Array.Copy(bs.Buf, bs.BufOffset + (bs.Pos >> 3), _maindata, bytesHave, frameBytes);
            bs.Init(_maindata, 0, bytesHave + frameBytes);
            return _reserv >= mainDataBegin;
        }

        // ====================================================================
        // Layer I/II
        // ====================================================================

        private sealed class L12ScaleInfo
        {
            public readonly float[] Scf = new float[3 * 64];
            public int TotalBands, StereoBands;
            public readonly byte[] Bitalloc = new byte[64];
            public readonly byte[] Scfcod = new byte[64];
        }

        private int[,] _l12Alloc;
        private int _l12AllocRows;

        private void L12SubbandAllocTable(byte[] hdr, L12ScaleInfo sci)
        {
            int mode = HdrGetStereoMode(hdr, 0);
            int stereoBands = mode == ModeMono ? 0 : (mode == ModeJointStereo ? (HdrGetStereoModeExt(hdr, 0) << 2) + 4 : 32);
            int nbands;
            int[,] alloc;
            int rows;

            if (HdrIsLayer1(hdr, 0))
            {
                alloc = new int[,] { { 76, 4, 32 } };
                rows = 1;
                nbands = 32;
            }
            else if (!HdrTestMpeg1(hdr, 0))
            {
                alloc = new int[,] { { 60, 4, 4 }, { 44, 3, 7 }, { 44, 2, 19 } };
                rows = 3;
                nbands = 30;
            }
            else
            {
                alloc = new int[,] { { 0, 4, 3 }, { 16, 4, 8 }, { 32, 3, 12 }, { 40, 2, 7 } };
                rows = 4;
                int sampleRateIdx = HdrGetSampleRateIdx(hdr, 0);
                int kbps = HdrBitrateKbps(hdr, 0) >> (mode != ModeMono ? 1 : 0);
                if (kbps == 0) kbps = 192; // free-format

                nbands = 27;
                if (kbps < 56)
                {
                    alloc = new int[,] { { 44, 4, 2 }, { 44, 3, 10 } };
                    rows = 2;
                    nbands = sampleRateIdx == 2 ? 12 : 8;
                }
                else if (kbps >= 96 && sampleRateIdx != 1)
                {
                    nbands = 30;
                }
            }

            sci.TotalBands = nbands;
            sci.StereoBands = Math.Min(stereoBands, nbands);
            _l12Alloc = alloc;
            _l12AllocRows = rows;
        }

        private void L12ReadScalefactors(ref Bs bs, byte[] pba, byte[] scfcod, int bands, float[] scf)
        {
            var deq = Mp3Tables.DeqL12;
            int i, m;
            int scfIdx = 0;
            for (i = 0; i < bands; i++)
            {
                float s = 0;
                int ba = pba[i];
                int mask = ba != 0 ? 4 + ((19 >> scfcod[i]) & 3) : 0;
                for (m = 4; m != 0; m >>= 1)
                {
                    if ((mask & m) != 0)
                    {
                        int b = (int)GetBits(ref bs, 6);
                        s = deq[ba * 3 - 6 + b % 3] * (1 << 21 >> b / 3);
                    }
                    scf[scfIdx++] = s;
                }
            }
        }

        private void L12ReadScaleInfo(byte[] hdr, ref Bs bs, L12ScaleInfo sci)
        {
            var baTab = Mp3Tables.BitAllocCodeTab;
            L12SubbandAllocTable(hdr, sci);

            int i, k = 0, baBits = 0;
            int baCodeOff = 0;
            int allocRow = 0;

            for (i = 0; i < sci.TotalBands; i++)
            {
                byte ba;
                if (i == k)
                {
                    k += _l12Alloc[allocRow, 2];
                    baBits = _l12Alloc[allocRow, 1];
                    baCodeOff = _l12Alloc[allocRow, 0];
                    allocRow++;
                }
                ba = baTab[baCodeOff + (int)GetBits(ref bs, baBits)];
                sci.Bitalloc[2 * i] = ba;
                if (i < sci.StereoBands)
                {
                    ba = baTab[baCodeOff + (int)GetBits(ref bs, baBits)];
                }
                sci.Bitalloc[2 * i + 1] = sci.StereoBands != 0 ? ba : (byte)0;
            }

            for (i = 0; i < 2 * sci.TotalBands; i++)
            {
                sci.Scfcod[i] = sci.Bitalloc[i] != 0 ? (byte)(HdrIsLayer1(hdr, 0) ? 2 : (int)GetBits(ref bs, 2)) : (byte)6;
            }

            L12ReadScalefactors(ref bs, sci.Bitalloc, sci.Scfcod, sci.TotalBands * 2, sci.Scf);

            for (i = sci.StereoBands; i < sci.TotalBands; i++)
            {
                sci.Bitalloc[2 * i + 1] = 0;
            }
        }

        private int L12DequantizeGranule(float[] grbuf, int grOff, ref Bs bs, L12ScaleInfo sci, int groupSize)
        {
            int i, j, k, choff = 576;
            for (j = 0; j < 4; j++)
            {
                int dst = grOff + groupSize * j;
                for (i = 0; i < 2 * sci.TotalBands; i++)
                {
                    int ba = sci.Bitalloc[i];
                    if (ba != 0)
                    {
                        if (ba < 17)
                        {
                            int half = (1 << (ba - 1)) - 1;
                            for (k = 0; k < groupSize; k++)
                            {
                                grbuf[dst + k] = (int)GetBits(ref bs, ba) - half;
                            }
                        }
                        else
                        {
                            uint mod = (uint)(2 << (ba - 17)) + 1;   // 3, 5, 9
                            uint code = GetBits(ref bs, (int)(mod + 2 - (mod >> 3)));  // 5, 7, 10
                            for (k = 0; k < groupSize; k++, code /= mod)
                            {
                                grbuf[dst + k] = (int)(code % mod - mod / 2);
                            }
                        }
                    }
                    dst += choff;
                    choff = 18 - choff;
                }
            }
            return groupSize * 4;
        }

        private static void L12ApplyScf384(L12ScaleInfo sci, float[] scf, int scfOff, float[] dst, int dstOff)
        {
            int i, k;
            Array.Copy(dst, dstOff + 576 + sci.StereoBands * 18, dst, dstOff + sci.StereoBands * 18, (sci.TotalBands - sci.StereoBands) * 18);
            int d = dstOff, s = scfOff;
            for (i = 0; i < sci.TotalBands; i++, d += 18, s += 6)
            {
                for (k = 0; k < 12; k++)
                {
                    dst[d + k] *= scf[s];
                    dst[d + k + 576] *= scf[s + 3];
                }
            }
        }

        // ====================================================================
        // 帧解码主入口 (对应 mp3dec_decode_frame)
        // ====================================================================

        /// <summary>
        /// 解码一帧。返回解码的每声道样本数 (0=无数据), pcm 为交错输出 (≥1152*2)。
        /// </summary>
        public int DecodeFrame(byte[] mp3, int mp3Offset, int mp3Bytes, float[] pcm, FrameInfo info)
        {
            int i = 0, igr, frameSize = 0;
            bool success = true;
            int hdrOff;

            if (mp3Bytes > 4 && _header[0] == 0xff && HdrCompareCached(_header, mp3, mp3Offset))
            {
                frameSize = HdrFrameBytes(mp3, mp3Offset, _freeFormatBytes) + HdrPadding(mp3, mp3Offset);
                if (frameSize != mp3Bytes && (frameSize + HdrSize > mp3Bytes || !HdrCompareAt(mp3, mp3Offset, mp3Offset + frameSize)))
                {
                    frameSize = 0;
                }
            }
            if (frameSize == 0)
            {
                Reset();
                i = FindFrame(mp3, mp3Offset, mp3Bytes, ref _freeFormatBytes, out frameSize);
                if (frameSize == 0 || i + frameSize > mp3Bytes)
                {
                    info.FrameBytes = i;
                    return 0;
                }
            }

            hdrOff = mp3Offset + i;
            Array.Copy(mp3, hdrOff, _header, 0, HdrSize);
            info.FrameBytes = i + frameSize;
            info.FrameOffset = i;
            info.Channels = HdrIsMono(mp3, hdrOff) ? 1 : 2;
            info.SampleRate = HdrSampleRateHz(mp3, hdrOff);
            info.Layer = 4 - HdrGetLayer(mp3, hdrOff);
            info.BitrateKbps = HdrBitrateKbps(mp3, hdrOff);

            var bsFrame = new Bs();
            bsFrame.Init(mp3, hdrOff + HdrSize, frameSize - HdrSize);
            if (HdrIsCrc(mp3, hdrOff))
            {
                GetBits(ref bsFrame, 16);
            }

            if (info.Layer == 3)
            {
                var grInfo = new[] { new GrInfo(), new GrInfo(), new GrInfo(), new GrInfo() };
                int mainDataBegin = L3ReadSideInfo(ref bsFrame, grInfo, 0, _header);
                if (mainDataBegin < 0 || bsFrame.Pos > bsFrame.Limit)
                {
                    Reset();
                    return 0;
                }
                success = L3RestoreReservoir(ref bsFrame, mainDataBegin);
                if (success)
                {
                    var grbuf = new float[2 * 576];
                    var scf = new float[40];
                    var syn = new float[(18 + 15) * 2 * 32];
                    var istPos = new byte[2][];
                    istPos[0] = new byte[39]; istPos[1] = new byte[39];
                    int pcmOff = 0;
                    for (igr = 0; igr < (HdrTestMpeg1(_header, 0) ? 2 : 1); igr++, pcmOff += 576 * info.Channels)
                    {
                        Array.Clear(grbuf, 0, grbuf.Length);
                        L3Decode(_header, ref bsFrame, grInfo, igr * info.Channels, info.Channels, grbuf, istPos, scf, syn);
                        Mp3dSynthGranule(_qmfState, grbuf, 0, 18, info.Channels, pcm, pcmOff, syn);
                    }
                }
                L3SaveReservoir(ref bsFrame);
            }
            else
            {
                var sci = new L12ScaleInfo();
                L12ReadScaleInfo(_header, ref bsFrame, sci);

                var grbuf = new float[2 * 576];
                var syn = new float[(18 + 15) * 2 * 32];
                int ii = 0, pcmOff = 0;
                for (igr = 0; igr < 3; igr++)
                {
                    if (12 == (ii += L12DequantizeGranule(grbuf, ii, ref bsFrame, sci, info.Layer | 1)))
                    {
                        ii = 0;
                        L12ApplyScf384(sci, sci.Scf, igr, grbuf, 0);
                        Mp3dSynthGranule(_qmfState, grbuf, 0, 12, info.Channels, pcm, pcmOff, syn);
                        Array.Clear(grbuf, 0, grbuf.Length);
                        pcmOff += 384 * info.Channels;
                    }
                    if (bsFrame.Pos > bsFrame.Limit)
                    {
                        Reset();
                        return 0;
                    }
                }
            }
            return success ? HdrFrameSamples(_header, 0) : 0;
        }
    }
}
