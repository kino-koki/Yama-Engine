// ============================================================================
// YamaEngine FlacDecoder.cs
// yama引擎 - FLAC (Free Lossless Audio Codec) 完整解码器
// 实现: 'fLaC' 元数据块解析(STREAMINFO解析, 其余块跳过)、
//       帧头解析(同步码/块大小/采样率/声道分配/位深/UTF-8帧号/CRC-8)、
//       子帧解码(Constant/Verbatim/Fixed 0-4阶/LPC任意阶 + 浪费位)、
//       分区Rice残差解码(含转义码)、声道去相关(左/侧 右/侧 中/侧)、
//       CRC-16 帧校验、STREAMINFO MD5 位精确校验。
// 输出: 交错 float[-1,1) (与 WavDecoder.DecodeAll 一致)。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace YamaEngine.Audio
{
    /// <summary>
    /// FLAC 解码器 - 完整实现 (帧级解码 + 位精确 MD5 校验)。
    /// </summary>
    public sealed class FlacDecoder
    {
        private byte[] _fileData;
        private FlacInfo _info;
        private byte[] _streamMd5;
        private int _audioStart;       // 音频帧区起始偏移
        private long _decodedSamples;  // 已解码帧数(单声道计)

        public FlacInfo Info => _info;
        /// <summary>STREAMINFO 中声明的音频流 MD5 (16字节, 全零表示未知)。</summary>
        public byte[] StreamMd5 => _streamMd5;

        /// <summary>
        /// FLAC 流信息
        /// </summary>
        public sealed class FlacInfo
        {
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public int BitsPerSample { get; set; }
            public long TotalSamples { get; set; }
            public int MinBlockSize { get; set; }
            public int MaxBlockSize { get; set; }
            public int MinFrameSize { get; set; }
            public int MaxFrameSize { get; set; }
            public double DurationSeconds { get; set; }
        }

        /// <summary>
        /// FLAC元数据块类型
        /// </summary>
        private enum MetadataBlockType : int
        {
            StreamInfo = 0,
            Padding = 1,
            Application = 2,
            SeekTable = 3,
            VorbisComment = 4,
            CueSheet = 5,
            Picture = 6
        }

        public FlacInfo Load(byte[] data)
        {
            _fileData = data ?? throw new ArgumentNullException(nameof(data));
            _info = new FlacInfo();
            _streamMd5 = new byte[16];
            _audioStart = 0;
            _decodedSamples = 0;

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // fLaC标记
            string marker = new string(reader.ReadChars(4));
            if (marker != "fLaC") throw new InvalidDataException("无效的FLAC文件: 缺少fLaC标记");

            // 解析元数据块
            bool lastBlock = false;
            while (!lastBlock && ms.Position + 4 <= ms.Length)
            {
                byte blockHeader = reader.ReadByte();
                lastBlock = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                int blockLength = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
                if (blockLength < 0 || ms.Position + blockLength > ms.Length)
                    throw new InvalidDataException($"无效的FLAC文件: 元数据块长度越界 ({blockLength})");

                long blockStart = ms.Position;

                if (blockType == (int)MetadataBlockType.StreamInfo)
                {
                    if (blockLength < 34) throw new InvalidDataException("无效的FLAC文件: STREAMINFO块过短");
                    ParseStreamInfo(reader);
                }

                ms.Position = blockStart + blockLength;
                if (lastBlock) _audioStart = (int)ms.Position;
            }

            if (_info.SampleRate <= 0)
                throw new InvalidDataException("无效的FLAC文件: 缺少STREAMINFO或采样率非法");

            _info.DurationSeconds = _info.SampleRate > 0
                ? (double)_info.TotalSamples / _info.SampleRate
                : 0;

            return _info;
        }

        public FlacInfo Load(string path) => Load(File.ReadAllBytes(path));

        /// <summary>
        /// 解析STREAMINFO块 (34字节)
        /// </summary>
        private void ParseStreamInfo(BinaryReader reader)
        {
            _info.MinBlockSize = reader.ReadUInt16();
            _info.MaxBlockSize = reader.ReadUInt16();
            _info.MinFrameSize = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
            _info.MaxFrameSize = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

            // 采样率(20bit) | 声道数-1(3bit) | 位深-1(5bit) | 总样本数(36bit)
            int sr1 = reader.ReadByte();
            int sr2 = reader.ReadByte();
            int sr3 = reader.ReadByte();
            _info.SampleRate = (sr1 << 12) | (sr2 << 4) | (sr3 >> 4);

            int chBps = sr3 & 0x0F;
            _info.Channels = ((chBps >> 1) & 0x07) + 1;
            int bpsByte = reader.ReadByte();
            _info.BitsPerSample = ((chBps & 0x01) << 4 | (bpsByte >> 4)) + 1;

            // 总样本数(36bit): 高4位为 bpsByte 低4位, 后跟4个完整字节
            long ts1 = bpsByte & 0x0F;
            long ts2 = reader.ReadByte();
            long ts3 = reader.ReadByte();
            long ts4 = reader.ReadByte();
            long ts5 = reader.ReadByte();
            _info.TotalSamples = (ts1 << 32) | (ts2 << 24) | (ts3 << 16) | (ts4 << 8) | ts5;

            // MD5(16字节)
            _streamMd5 = reader.ReadBytes(16);
        }

        // ====================================================================
        // 位读取器 (MSB-first, FLAC 规定位流为高位先传)
        // ====================================================================

        /// <summary>
        /// MSB-first 位读取器, 64 位缓存。
        /// </summary>
        private struct BitReader
        {
            private byte[] _buf;
            private int _pos;        // 下一位的绝对位位置
            private int _limit;      // 可读位上限
            private ulong _cache;    // 缓存位 (低位是较新的字节)
            private int _cacheBits;  // 缓存有效位数

            public void Init(byte[] buf, int byteOffset, int byteLimitEnd)
            {
                _buf = buf;
                _pos = byteOffset * 8;
                _limit = byteLimitEnd * 8;
                _cache = 0;
                _cacheBits = 0;
            }

            public int BitPosition => _pos;

            private void Fill()
            {
                while (_cacheBits <= 56)
                {
                    int byteIdx = (_pos + _cacheBits) >> 3;
                    if (byteIdx >= _buf.Length || byteIdx * 8 >= _limit) break;
                    _cache = (_cache << 8) | _buf[byteIdx];
                    _cacheBits += 8;
                }
            }

            /// <summary>读取 n 位 (高位先传), n ∈ [0,32]。</summary>
            public uint ReadBits(int n)
            {
                if (n == 0) return 0;
                if (_pos + n > _limit)
                    throw new InvalidDataException("FLAC位流越界(数据截断)");
                if (_cacheBits < n) Fill();
                if (_cacheBits < n)
                    throw new InvalidDataException("FLAC位流越界(缓存不足)");
                uint val = (uint)((_cache >> (_cacheBits - n)) & ((1UL << n) - 1));
                _cacheBits -= n;
                _pos += n;
                return val;
            }

            /// <summary>读取 1 位。</summary>
            public uint ReadBit() => ReadBits(1);

            /// <summary>带符号读取 (n 位二进制补码)。</summary>
            public int ReadBitsSigned(int n)
            {
                uint v = ReadBits(n);
                if (n == 0) return 0;
                if ((v & (1u << (n - 1))) != 0)
                    return unchecked((int)(v | (0xFFFFFFFFu << n)));
                return (int)v;
            }

            /// <summary>
            /// 读取 Rice 编码残差 (一元商 + k 位余数, zigzag 映射)。
            /// </summary>
            public int ReadRice(int k)
            {
                int quotient = 0;
                while (ReadBit() == 0)
                {
                    quotient++;
                    if (quotient > (1 << 20)) throw new InvalidDataException("FLAC Rice商超限");
                }
                uint remainder = ReadBits(k);
                long value = ((long)quotient << k) | remainder;
                // zigzag: 0→0, 1→-1, 2→1, 3→-2, ...
                return (value & 1) == 0 ? (int)(value >> 1) : (int)-((value + 1) >> 1);
            }
        }

        // ====================================================================
        // CRC 校验
        // ====================================================================

        /// <summary>CRC-8 (多项式 0x07, FLAC 帧头校验)。</summary>
        private static byte Crc8(byte[] data, int offset, int count)
        {
            byte crc = 0;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
            return crc;
        }

        /// <summary>CRC-16 (多项式 0x8005, FLAC 整帧校验)。</summary>
        private static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int b = 0; b < 8; b++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
            return crc;
        }

        // ====================================================================
        // Rice / 预测器 (保留原有公开静态 API, 供外部与测试使用)
        // ====================================================================

        /// <summary>
        /// Rice解码 (Golomb-Rice编码), 用于FLAC残差信号解码。
        /// </summary>
        public static int RiceDecode(byte[] data, ref int bitOffset, int k)
        {
            if (data == null || bitOffset < 0 || k < 0 || k > 31)
                throw new InvalidDataException("无效的Rice解码参数");
            int totalBits = data.Length * 8;
            if (bitOffset >= totalBits)
                throw new InvalidDataException($"Rice位偏移越界: {bitOffset} >= {totalBits}");

            // 读取quotient(一元编码: 商个0后接1)
            int quotient = 0;
            while (true)
            {
                if (bitOffset >= totalBits)
                    throw new InvalidDataException($"Rice位偏移越界: {bitOffset} >= {totalBits}");
                int bit = (data[bitOffset >> 3] >> (7 - (bitOffset & 7))) & 1;
                bitOffset++;
                if (bit == 1) break;
                quotient++;
            }

            // 读取remainder(k位)
            int remainder = 0;
            for (int i = k - 1; i >= 0; i--)
            {
                if (bitOffset >= totalBits)
                    throw new InvalidDataException($"Rice位偏移越界: {bitOffset} >= {totalBits}");
                int bit = (data[bitOffset >> 3] >> (7 - (bitOffset & 7))) & 1;
                bitOffset++;
                remainder |= bit << i;
            }

            int value = (quotient << k) | remainder;

            // zigzag映射: 0→0, 1→-1, 2→1, 3→-2, ...
            if ((value & 1) == 0)
                return value >> 1;
            return -((value + 1) >> 1);
        }

        /// <summary>
        /// FLAC固定预测器(0-4阶)
        /// </summary>
        public static int FixedPredictor(int order, int[] samples, int n)
        {
            switch (order)
            {
                case 0: return 0;
                case 1: return samples[n - 1];
                case 2: return 2 * samples[n - 1] - samples[n - 2];
                case 3: return 3 * samples[n - 1] - 3 * samples[n - 2] + samples[n - 3];
                case 4: return 4 * samples[n - 1] - 6 * samples[n - 2] + 4 * samples[n - 3] - samples[n - 4];
                default: return 0;
            }
        }

        /// <summary>
        /// FLAC LPC (线性预测编码) 预测
        /// sample[n] = residual[n] + (sum(lpc[i] * sample[n-1-i]) >> shift)
        /// </summary>
        public static int LpcPredict(int[] coefficients, int shift, int[] samples, int n, int order)
        {
            long prediction = 0;
            for (int i = 0; i < order; i++)
                prediction += (long)coefficients[i] * samples[n - 1 - i];
            prediction >>= shift;
            return (int)prediction;
        }

        // ====================================================================
        // 解码入口
        // ====================================================================

        /// <summary>
        /// 解码全部样本为 float[-1,1] (声道交错)。
        /// 若 STREAMINFO 携带 MD5, 解码后自动校验位精确性 (不匹配抛 InvalidDataException)。
        /// </summary>
        public float[] DecodeAll()
        {
            if (_fileData == null || _info == null)
                throw new InvalidOperationException("未加载FLAC文件");

            int channels = _info.Channels;
            int bps = _info.BitsPerSample;
            long totalSamples = _info.TotalSamples;

            // 工业级防御: 输出上限 1 亿浮点 (400MB)
            const long MaxOutputSamples = 100_000_000;
            if (totalSamples < 0 || (long)channels * Math.Max(totalSamples, 0) > MaxOutputSamples && totalSamples > 0)
                throw new InvalidDataException($"FLAC输出样本数过大 ({totalSamples}×{channels})");

            float[] output;
            if (totalSamples > 0)
                output = new float[totalSamples * channels];
            else
                output = new float[65536]; // 未知长度: 动态增长
            int outIdx = 0;

            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            int md5BytesPerSample = (bps + 7) / 8;

            int pos = _audioStart;
            int fileLen = _fileData.Length;
            var md5Chunk = new List<byte>(65536);
            bool done = false;

            while (!done && pos + 2 < fileLen)
            {
                // 查找帧同步码 11111111111110 (0xFF 后接 0b111111xx)
                if (_fileData[pos] != 0xFF || (_fileData[pos + 1] & 0xFC) != 0xF8)
                {
                    pos++;
                    continue;
                }

                int frameStart = pos;
                var frame = DecodeFrame(_fileData, frameStart, out int frameBytes);
                if (frame == null)
                    throw new InvalidDataException($"FLAC帧解码失败 (偏移 {frameStart})");
                pos = frameStart + frameBytes;

                // 交错输出 + MD5 累加 (按原始位深有符号小端)
                int frameSamples = frame.BlockSize;
                for (int i = 0; i < frameSamples; i++)
                {
                    if (totalSamples > 0 && _decodedSamples + i >= totalSamples)
                    {
                        done = true;
                        break;
                    }
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int s = frame.Channels[ch][i];
                        if (outIdx >= output.Length)
                        {
                            if (output.Length * 2 > MaxOutputSamples)
                                throw new InvalidDataException("FLAC输出样本数超限");
                            Array.Resize(ref output, output.Length * 2);
                        }
                        output[outIdx++] = s / 2147483648.0f * (1 << (32 - bps));

                        for (int by = 0; by < md5BytesPerSample; by++)
                            md5Chunk.Add((byte)((s >> (by << 3)) & 0xFF));
                    }
                }
                _decodedSamples += frameSamples;
                if (md5Chunk.Count > 0)
                {
                    md5.AppendData(md5Chunk.ToArray());
                    md5Chunk.Clear();
                }
            }

            // MD5 校验 (STREAMINFO 声明的 MD5 非全零时)
            bool md5AllZero = true;
            foreach (byte b in _streamMd5) { if (b != 0) { md5AllZero = false; break; } }
            if (!md5AllZero)
            {
                byte[] actual = md5.GetHashAndReset();
                for (int i = 0; i < 16; i++)
                {
                    if (actual[i] != _streamMd5[i])
                        throw new InvalidDataException("FLAC MD5 校验失败: 解码数据与 STREAMINFO 声明不一致");
                }
            }

            if (outIdx < output.Length)
                Array.Resize(ref output, outIdx);
            return output;
        }

        /// <summary>
        /// 解码单个FLAC帧 (从 offset 的同步码开始)。
        /// </summary>
        private FlacFrame DecodeFrame(byte[] data, int offset, out int frameBytes)
        {
            frameBytes = 0;
            var br = new BitReader();
            br.Init(data, offset, data.Length);

            // ── 帧头 ──
            uint sync = br.ReadBits(14);
            if (sync != 0x3FFE) return null; // 11111111111110
            if (br.ReadBit() != 0) return null;      // 保留位
            br.ReadBit();                             // 阻塞策略 (0=帧号, 1=样本号)
            uint blockSizeCode = br.ReadBits(4);
            uint sampleRateCode = br.ReadBits(4);
            uint channelAssign = br.ReadBits(4);
            uint bpsCode = br.ReadBits(3);
            if (br.ReadBit() != 0) return null;      // 保留位

            // 块大小/采样率: 附加字节在 UTF-8 编码号之后读取 (FLAC 规定顺序)
            int blockSize = 0;
            bool extraBlockSize = false;
            switch (blockSizeCode)
            {
                case 0: return null; // 保留
                case 1: blockSize = 192; break;
                case 2: blockSize = 576; break;
                case 3: blockSize = 1152; break;
                case 4: blockSize = 2304; break;
                case 5: blockSize = 4608; break;
                case 6: extraBlockSize = true; break; // 8位
                case 7: extraBlockSize = true; break; // 16位
                default: blockSize = 256 << (int)(blockSizeCode - 8); break;
            }

            int sampleRate = _info.SampleRate;
            int extraSampleRateBits = 0;
            switch (sampleRateCode)
            {
                case 12: extraSampleRateBits = 8; break;  // kHz
                case 13: extraSampleRateBits = 16; break; // Hz
                case 14: extraSampleRateBits = 16; break; // Hz/10
                case 15: return null; // 保留
                default:
                    if (sampleRateCode != 0)
                    {
                        int[] srt = { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };
                        sampleRate = srt[sampleRateCode];
                    }
                    break;
            }

            int channels;
            bool leftSide = false, rightSide = false, midSide = false;
            switch (channelAssign)
            {
                case 8: channels = 2; leftSide = true; break;
                case 9: channels = 2; rightSide = true; break;
                case 10: channels = 2; midSide = true; break;
                case 11: case 12: case 13: case 14: case 15: return null;
                default: channels = (int)channelAssign + 1; break;
            }

            int frameBps;
            switch (bpsCode)
            {
                case 0: frameBps = _info.BitsPerSample; break;
                case 1: frameBps = 8; break;
                case 2: frameBps = 12; break;
                case 3: return null; // 保留
                case 4: frameBps = 16; break;
                case 5: frameBps = 20; break;
                case 6: frameBps = 24; break;
                case 7: frameBps = 32; break;
                default: return null;
            }

            // UTF-8 编码的帧号/样本号 (变长)
            if (!ReadUtf8Coded(ref br, out long dbgNum)) return null;

            // 附加块大小/采样率字节 (位于编码号之后)
            if (extraBlockSize)
                blockSize = (int)br.ReadBits(blockSizeCode == 6 ? 8 : 16) + 1;
            switch (extraSampleRateBits)
            {
                case 8: sampleRate = (int)br.ReadBits(8) * 1000; break;
                case 16: sampleRate = (int)br.ReadBits(16); break;
            }
            if (sampleRateCode == 14) sampleRate *= 10;
            if (blockSize <= 0 || sampleRate <= 0)
                throw new InvalidDataException($"FLAC帧头块大小/采样率非法 ({blockSize}/{sampleRate})");

            // CRC-8 (帧头: 同步码起至本字段前的全部字节)
            int headerByteEnd = (br.BitPosition + 7) >> 3;
            byte crc8Got = (byte)br.ReadBits(8);
            byte crc8Calc = Crc8(data, offset, headerByteEnd - offset);
            if (crc8Got != crc8Calc)
                throw new InvalidDataException($"FLAC帧头CRC-8校验失败 (偏移 {offset}) got={crc8Got:X2} calc={crc8Calc:X2}");

            // ── 子帧解码 (侧声道位深 +1, 与 libFLAC 一致) ──
            var frame = new FlacFrame { BlockSize = blockSize, Channels = new int[channels][] };
            for (int ch = 0; ch < channels; ch++)
            {
                int chBps = frameBps;
                if ((leftSide && ch == 1) || (rightSide && ch == 0) || (midSide && ch == 1))
                    chBps++;
                frame.Channels[ch] = DecodeSubframe(ref br, blockSize, chBps);
            }

            // 字节对齐 + CRC-16 (整帧)
            int alignBits = (8 - (br.BitPosition & 7)) & 7;
            if (alignBits > 0 && br.ReadBits(alignBits) != 0)
                throw new InvalidDataException("FLAC帧尾填充位非零");
            int crc16Pos = br.BitPosition >> 3;
            ushort crc16Got = (ushort)br.ReadBits(16);
            ushort crc16Calc = Crc16(data, offset, crc16Pos - offset);
            if (crc16Got != crc16Calc)
                throw new InvalidDataException($"FLAC帧CRC-16校验失败 (偏移 {offset})");
            frameBytes = (br.BitPosition >> 3) - offset;

            // ── 声道去相关 ──
            if (leftSide)
            {
                // ch0=左, ch1=左-右 → 右 = 左 - 侧
                int[] left = frame.Channels[0], side = frame.Channels[1];
                for (int i = 0; i < blockSize; i++) side[i] = left[i] - side[i];
            }
            else if (rightSide)
            {
                // ch0=右-左, ch1=右 → 左 = 右 - 侧
                int[] side = frame.Channels[0], right = frame.Channels[1];
                for (int i = 0; i < blockSize; i++) side[i] = right[i] - side[i];
            }
            else if (midSide)
            {
                // ch0=中=(左+右)>>1, ch1=侧=左-右
                int[] mid = frame.Channels[0], side = frame.Channels[1];
                for (int i = 0; i < blockSize; i++)
                {
                    long m = mid[i], s = side[i];
                    long m2 = (m << 1) | (s & 1);
                    mid[i] = (int)((m2 + s) >> 1);
                    side[i] = (int)((m2 - s) >> 1);
                }
            }

            return frame;
        }

        private sealed class FlacFrame
        {
            public int BlockSize;
            public int[][] Channels;
        }

        /// <summary>
        /// 解码单个子帧 (含浪费位处理)。
        /// 布局与 libFLAC read_subframe_ 一致: 8位头(填充|类型|浪费位), 
        /// 预热样本在前, 其后为精度/移位/系数 (LPC) 与熵编码残差。
        /// </summary>
        private int[] DecodeSubframe(ref BitReader br, int blockSize, int frameBps)
        {
            uint head = br.ReadBits(8);
            uint type = (head >> 1) & 0x7F;
            int wasted = (int)(head & 1);

            if (wasted != 0)
            {
                // 一元编码的 (浪费位数-1): 0...01
                wasted = 1;
                while (br.ReadBit() == 0)
                {
                    wasted++;
                    if (wasted > 64) throw new InvalidDataException("FLAC浪费位超限");
                }
            }

            int subBps = frameBps - wasted;
            if (subBps <= 0) throw new InvalidDataException("FLAC子帧位深非法");

            int[] samples = new int[blockSize];

            if (type == 0)
            {
                // Constant: 单值填充
                int v = br.ReadBitsSigned(subBps) << wasted;
                for (int i = 0; i < blockSize; i++) samples[i] = v;
                return samples;
            }
            if (type == 1)
            {
                // Verbatim: 原样存储
                for (int i = 0; i < blockSize; i++)
                    samples[i] = br.ReadBitsSigned(subBps) << wasted;
                return samples;
            }
            if (type >= 8 && type <= 15)
            {
                // Fixed predictor, order = type&7 (0..4 合法, 5-7保留)
                int order = (int)(type & 7);
                if (order > 4) throw new InvalidDataException($"FLAC固定预测器阶数非法 ({order})");
                if (blockSize <= order) throw new InvalidDataException("FLAC块大小不大于预测阶数");

                // 预热样本 (在前)
                for (int i = 0; i < order; i++)
                    samples[i] = br.ReadBitsSigned(subBps) << wasted;

                // 分区 Rice 残差
                ReadResiduals(ref br, samples, blockSize, order, wasted);

                // 固定预测器重建
                for (int n = order; n < blockSize; n++)
                    samples[n] += FixedPredictor(order, samples, n);
                return samples;
            }
            if (type >= 32)
            {
                // LPC, order = (type&31)+1 (1..32)
                int order = (int)(type & 31) + 1;
                if (blockSize <= order) throw new InvalidDataException("FLAC块大小不大于预测阶数");

                // 预热样本 (在前)
                for (int i = 0; i < order; i++)
                    samples[i] = br.ReadBitsSigned(subBps) << wasted;

                // 系数量化精度 (4位, 1111=非法)
                int precision = (int)br.ReadBits(4) + 1;
                if (precision == 16) throw new InvalidDataException("FLAC LPC精度非法(1111保留)");
                // 移位量 (5位有符号, 非负)
                int shift = br.ReadBitsSigned(5);
                if (shift < 0) throw new InvalidDataException("FLAC LPC移位量为负");
                int[] coefs = new int[order];
                for (int i = 0; i < order; i++)
                    coefs[i] = br.ReadBitsSigned(precision);

                // 分区 Rice 残差
                ReadResiduals(ref br, samples, blockSize, order, wasted);

                // LPC 重建
                for (int n = order; n < blockSize; n++)
                {
                    long pred = 0;
                    for (int i = 0; i < order; i++)
                        pred += (long)coefs[i] * samples[n - 1 - i];
                    samples[n] += (int)(pred >> shift);
                }
                return samples;
            }
            throw new InvalidDataException($"FLAC子帧类型非法 ({type})");
        }

        /// <summary>
        /// 读取分区 Rice 编码残差 (含 RICE/RICE2 与转义码), 累加到 samples。
        /// 与 libFLAC read_residual_partitioned_rice_ 一致。
        /// </summary>
        private void ReadResiduals(ref BitReader br, int[] samples, int blockSize, int order, int wasted)
        {
            // 2位熵编码方法: 0=PARTITIONED_RICE, 1=PARTITIONED_RICE2
            uint method = br.ReadBits(2);
            if (method > 1)
                throw new InvalidDataException($"FLAC熵编码方法非法 ({method})");
            int paramBits = method == 0 ? 4 : 5;
            uint escape = method == 0 ? 15u : 31u;

            int partitionOrder = (int)br.ReadBits(4);
            int partitions = 1 << partitionOrder;
            if ((blockSize >> partitionOrder) < order || blockSize % partitions != 0)
                throw new InvalidDataException("FLAC分区阶数与块大小不一致");
            int partSize = blockSize >> partitionOrder;

            int pos = order; // 残差对齐到预热样本之后
            for (int p = 0; p < partitions; p++)
            {
                uint riceParam = br.ReadBits(paramBits);
                int count = partSize - (p == 0 ? order : 0);
                if (riceParam < escape)
                {
                    for (int i = 0; i < count; i++)
                        samples[pos + i] += br.ReadRice((int)riceParam) << wasted;
                }
                else
                {
                    // 转义: 5位原始位深, 0=全零残差
                    int rawBits = (int)br.ReadBits(5);
                    if (rawBits == 0)
                    {
                        // 残差全零, 样本位置保持
                    }
                    else
                    {
                        if (rawBits > 32) throw new InvalidDataException("FLAC转义原始位深超限");
                        for (int i = 0; i < count; i++)
                            samples[pos + i] += br.ReadBitsSigned(rawBits) << wasted;
                    }
                }
                pos += count;
            }
        }

        /// <summary>
        /// 读取UTF-8变长编码数 (FLAC帧号/样本号)。
        /// </summary>
        private static bool ReadUtf8Coded(ref BitReader br, out long value)
        {
            value = 0;
            uint b0 = br.ReadBits(8);
            int extra;
            if ((b0 & 0x80) == 0) { value = b0; return true; }
            if ((b0 & 0xE0) == 0xC0) { value = b0 & 0x1F; extra = 1; }
            else if ((b0 & 0xF0) == 0xE0) { value = b0 & 0x0F; extra = 2; }
            else if ((b0 & 0xF8) == 0xF0) { value = b0 & 0x07; extra = 3; }
            else if ((b0 & 0xFC) == 0xF8) { value = b0 & 0x03; extra = 4; }
            else if ((b0 & 0xFE) == 0xFC) { value = b0 & 0x01; extra = 5; }
            else return false;

            for (int i = 0; i < extra; i++)
            {
                uint b = br.ReadBits(8);
                if ((b & 0xC0) != 0x80) return false;
                value = (value << 6) | (b & 0x3F);
            }
            return true;
        }
    }
}
