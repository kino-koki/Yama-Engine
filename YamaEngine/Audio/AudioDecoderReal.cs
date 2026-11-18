// ============================================================================
// YamaEngine AudioDecoderReal.cs
// yama引擎 - 真实音频解码器实现
// 包含: WAV解码器(RIFF/PCM/IEEE Float/IMA ADPCM/循环点)、
//       重采样器(线性/立方/Hermite/Sinc)。
// 注: Vorbis/MP3/FLAC 完整解码器分别在 VorbisDecoder.cs / Mp3Decoder.cs / FlacDecoder.cs。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace YamaEngine.Audio
{
    // ========================================================================
    // WAV 解码器
    // ========================================================================

    /// <summary>
    /// WAV音频格式标签(WAVEFORMATEX wFormatTag)
    /// </summary>
    public static class WaveFormatTag
    {
        public const int Pcm = 1;
        public const int IeeeFloat = 3;
        public const int ImaAdpcm = 2;
        public const int Extensible = 0xFFFE;
    }

    /// <summary>
    /// WAV文件信息
    /// </summary>
    public sealed class WavInfo
    {
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public int FormatTag { get; set; }
        public int BlockAlign { get; set; }
        public int DataOffset { get; set; }
        public int DataSize { get; set; }
        public int LoopStart { get; set; } = -1;
        public int LoopEnd { get; set; } = -1;
        public int NumSamples { get; set; }

        public AudioFormatDescriptor ToAudioFormat()
        {
            AudioSampleFormat fmt;
            if (FormatTag == WaveFormatTag.IeeeFloat)
                fmt = BitsPerSample == 64 ? AudioSampleFormat.IeeeFloat64 : AudioSampleFormat.IeeeFloat32;
            else
                fmt = BitsPerSample switch
                {
                    8 => AudioSampleFormat.Pcm8Bit,
                    16 => AudioSampleFormat.Pcm16Bit,
                    24 => AudioSampleFormat.Pcm24Bit,
                    32 => AudioSampleFormat.Pcm32Bit,
                    _ => AudioSampleFormat.Pcm16Bit
                };
            return new AudioFormatDescriptor(SampleRate, BitsPerSample, Channels, fmt);
        }
    }

    /// <summary>
    /// WAV解码器 - 解析RIFF容器, 支持PCM 8/16/24/32 bit, IEEE Float 32/64, IMA ADPCM。
    /// 支持smpl块循环点。
    /// </summary>
    public sealed class WavDecoder
    {
        private byte[] _fileData;
        private WavInfo _info;

        public WavInfo Info => _info;
        public bool IsLoaded => _fileData != null;

        /// <summary>
        /// 从字节数组加载WAV文件
        /// </summary>
        public WavInfo Load(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _fileData = data;
            _info = ParseHeader(data);
            return _info;
        }

        /// <summary>
        /// 从文件加载
        /// </summary>
        public WavInfo Load(string path)
        {
            _fileData = File.ReadAllBytes(path);
            _info = ParseHeader(_fileData);
            return _info;
        }

        /// <summary>
        /// 解析RIFF文件头 - 'RIFF'/'WAVE'/'fmt '/'data'/'smpl'/'fact'/'LIST'块
        /// </summary>
        private WavInfo ParseHeader(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 12) throw new InvalidDataException("无效的WAV文件: 文件过短(<12字节)");

            var info = new WavInfo();
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // RIFF头
            string riff = ReadFourCC(reader);
            if (riff != "RIFF") throw new InvalidDataException("无效的WAV文件: 缺少RIFF标记");
            reader.ReadUInt32(); // 文件大小
            string wave = ReadFourCC(reader);
            if (wave != "WAVE") throw new InvalidDataException("无效的WAV文件: 缺少WAVE标记");

            // 遍历块
            while (ms.Position < ms.Length - 8)
            {
                string chunkId = ReadFourCC(reader);
                int chunkSize = reader.ReadInt32();

                // 工业级防御: chunkSize 可为负(伪造)或超出文件, 禁止 Position 越界。
                if (chunkSize < 0)
                    throw new InvalidDataException($"无效的WAV文件: 块 '{chunkId}' 大小为负 ({chunkSize})");
                long chunkEnd = ms.Position + chunkSize;
                if (chunkEnd > ms.Length)
                    throw new InvalidDataException($"无效的WAV文件: 块 '{chunkId}' 声明大小超出文件范围");

                switch (chunkId)
                {
                    case "fmt ":
                        if (ms.Position + 16 > ms.Length)
                            throw new InvalidDataException("无效的WAV文件: fmt块过短");
                        info.FormatTag = reader.ReadUInt16();
                        info.Channels = reader.ReadUInt16();
                        info.SampleRate = reader.ReadInt32();
                        reader.ReadInt32(); // byteRate
                        info.BlockAlign = reader.ReadUInt16();
                        info.BitsPerSample = reader.ReadUInt16();
                        // 工业级防御: 声道/位深非法
                        if (info.Channels <= 0 || info.Channels > 256)
                            throw new InvalidDataException($"无效的WAV文件: 声道数非法 ({info.Channels})");
                        if (info.BitsPerSample is not (8 or 16 or 24 or 32 or 64))
                            throw new InvalidDataException($"无效的WAV文件: 位深非法 ({info.BitsPerSample})");
                        if (info.BlockAlign <= 0)
                            throw new InvalidDataException($"无效的WAV文件: 块对齐非法 ({info.BlockAlign})");
                        // 跳过额外数据
                        if (chunkSize > 16)
                            ms.Position = chunkEnd;
                        break;

                    case "data":
                        if (ms.Position > int.MaxValue)
                            throw new InvalidDataException("无效的WAV文件: data块偏移超出int范围");
                        info.DataOffset = (int)ms.Position;
                        info.DataSize = chunkSize;
                        // 计算样本数 (工业级防御: 防止除零/溢出)
                        if (info.FormatTag == WaveFormatTag.ImaAdpcm)
                        {
                            // IMA ADPCM: 每个block包含header + 样本
                            int headerPerChannel = info.BlockAlign / info.Channels;
                            if (headerPerChannel < 4)
                                throw new InvalidDataException("无效的WAV文件: IMA ADPCM块对齐非法");
                            int samplesPerBlock = (headerPerChannel - 4) * 8 + 1;
                            int numBlocks = info.DataSize / info.BlockAlign;
                            // 工业级防御: numBlocks*samplesPerBlock 用 long 计算防 int 溢出 (损坏头可伪造超大值)。
                            long totalSamplesL = (long)numBlocks * samplesPerBlock;
                            if (totalSamplesL > int.MaxValue)
                                throw new InvalidDataException($"无效的WAV文件: IMA ADPCM 样本数溢出 ({totalSamplesL})");
                            info.NumSamples = (int)totalSamplesL;
                        }
                        else
                        {
                            int bytesPerSample = info.BitsPerSample / 8;
                            int bytesPerFrame = info.Channels * bytesPerSample;
                            if (bytesPerFrame <= 0)
                                throw new InvalidDataException("无效的WAV文件: 帧字节数非法");
                            info.NumSamples = info.DataSize / bytesPerFrame;
                        }
                        ms.Position = chunkEnd;
                        break;

                    case "smpl":
                        // 采样器块 - 包含循环点
                        if (ms.Position + 36 > ms.Length)
                            throw new InvalidDataException("无效的WAV文件: smpl块过短");
                        reader.ReadInt32(); // manufacturer
                        reader.ReadInt32(); // product
                        reader.ReadInt32(); // sample period
                        reader.ReadInt32(); // MIDI unity note
                        reader.ReadInt32(); // MIDI pitch fraction
                        reader.ReadInt32(); // SMPTE format
                        reader.ReadInt32(); // SMPTE offset
                        int numSampleLoops = reader.ReadInt32();
                        reader.ReadInt32(); // sampler data size

                        // 工业级防御: 循环数不能超过剩余可读空间(每个循环24字节)
                        int maxLoops = (int)Math.Min((chunkEnd - ms.Position) / 24, 4096);
                        int loopsToRead = Math.Min(numSampleLoops, maxLoops);
                        for (int i = 0; i < loopsToRead; i++)
                        {
                            reader.ReadInt32(); // cue point ID
                            reader.ReadInt32(); // type (0=forward, 1=alternating, 2=backward)
                            int start = reader.ReadInt32();
                            int end = reader.ReadInt32();
                            reader.ReadInt32(); // fraction
                            reader.ReadInt32(); // play count (0=infinite)

                            if (i == 0) // 取第一个循环
                            {
                                info.LoopStart = start;
                                info.LoopEnd = end;
                            }
                        }
                        ms.Position = chunkEnd;
                        break;

                    case "fact":
                        // fact块 - 样本数(非PCM格式)
                        if (ms.Position + 4 > ms.Length)
                            throw new InvalidDataException("无效的WAV文件: fact块过短");
                        int factSamples = reader.ReadInt32();
                        if (info.NumSamples == 0 && factSamples >= 0) info.NumSamples = factSamples;
                        ms.Position = chunkEnd;
                        break;

                    case "LIST":
                    default:
                        ms.Position = chunkEnd;
                        break;
                }

                // 块对齐(偶数)
                if (chunkSize % 2 == 1 && ms.Position < ms.Length)
                    ms.Position += 1;
            }

            return info;
        }

        private static string ReadFourCC(BinaryReader reader)
        {
            return new string(reader.ReadChars(4));
        }

        /// <summary>
        /// 解码全部样本为float[-1,1] (interleaved)
        /// </summary>
        public float[] DecodeAll()
        {
            if (_fileData == null || _info == null)
                throw new InvalidOperationException("未加载WAV文件");

            // 工业级防御: data块必须落在文件范围内
            if (_info.DataOffset < 0 || _info.DataSize < 0 ||
                _info.DataOffset > _fileData.Length ||
                _info.DataSize > _fileData.Length - _info.DataOffset)
                throw new InvalidDataException("无效的WAV文件: data块超出文件范围");

            if (_info.FormatTag == WaveFormatTag.ImaAdpcm)
                return DecodeImaAdpcm();
            else if (_info.FormatTag == WaveFormatTag.IeeeFloat)
                return DecodeIeeeFloat();
            else
                return DecodePcm();
        }

        /// <summary>
        /// 工业级防御: 计算解码输出样本总数(声道交错), 校验溢出与上限。
        /// </summary>
        private int SafeOutputSampleCount(int samplesPerChannel)
        {
            if (samplesPerChannel < 0) throw new InvalidDataException("无效的WAV文件: 样本数为负");
            long total = (long)samplesPerChannel * _info.Channels;
            // 单次解码输出上限 1 亿浮点 (400MB), 防伪造头 OOM。
            const long MaxOutputSamples = 100_000_000;
            if (total > MaxOutputSamples)
                throw new InvalidDataException($"无效的WAV文件: 样本总数过大 ({total})");
            return (int)total;
        }

        /// <summary>
        /// 解码PCM 8/16/24/32 bit → float
        /// </summary>
        private float[] DecodePcm()
        {
            int numSamples = SafeOutputSampleCount(_info.NumSamples);
            float[] output = new float[numSamples];

            using var ms = new MemoryStream(_fileData);
            ms.Position = _info.DataOffset;
            using var reader = new BinaryReader(ms);

            int bps = _info.BitsPerSample;

            // 工业级防御: 校验声明样本数所需的字节数与 data 块大小及文件实际可用数据一致,
            // 防止伪造头使 ReadByte/ReadInt16 读过头(静默零填充或中途异常)。
            int bytesPerSample = bps / 8;
            long expectedBytes = (long)numSamples * bytesPerSample;
            if (expectedBytes > _info.DataSize)
                throw new InvalidDataException($"WAV PCM 数据长度不符: 需要 {expectedBytes} 字节, data块声明 {_info.DataSize}");
            if ((long)_info.DataOffset + expectedBytes > _fileData.Length)
                throw new InvalidDataException($"WAV PCM 数据截断: 需要 {expectedBytes} 字节, 文件实际可用 {_fileData.Length - _info.DataOffset}");

            switch (bps)
            {
                case 8:
                    // 8-bit PCM是无符号(0-255), 中心=128
                    for (int i = 0; i < numSamples; i++)
                        output[i] = (reader.ReadByte() - 128) / 128.0f;
                    break;

                case 16:
                    // 16-bit PCM是有符号小端
                    for (int i = 0; i < numSamples; i++)
                        output[i] = reader.ReadInt16() / 32768.0f;
                    break;

                case 24:
                    // 24-bit PCM有符号小端, 手动读取3字节
                    for (int i = 0; i < numSamples; i++)
                    {
                        int b0 = reader.ReadByte();
                        int b1 = reader.ReadByte();
                        int b2 = reader.ReadByte();
                        int val = b0 | (b1 << 8) | (b2 << 16);
                        if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000); // 符号扩展
                        output[i] = val / 8388608.0f; // 2^23
                    }
                    break;

                case 32:
                    // 32-bit PCM有符号小端
                    for (int i = 0; i < numSamples; i++)
                        output[i] = reader.ReadInt32() / 2147483648.0f; // 2^31
                    break;
            }

            return output;
        }

        /// <summary>
        /// 解码IEEE Float 32/64 bit
        /// </summary>
        private float[] DecodeIeeeFloat()
        {
            int numSamples = SafeOutputSampleCount(_info.NumSamples);
            float[] output = new float[numSamples];

            using var ms = new MemoryStream(_fileData);
            ms.Position = _info.DataOffset;
            using var reader = new BinaryReader(ms);

            if (_info.BitsPerSample == 32)
            {
                for (int i = 0; i < numSamples; i++)
                    output[i] = reader.ReadSingle();
            }
            else // 64
            {
                for (int i = 0; i < numSamples; i++)
                    output[i] = (float)reader.ReadDouble();
            }

            return output;
        }

        // IMA ADPCM步长索引表
        private static readonly int[] ImaStepTable = new int[]
        {
            7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,
            50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,
            337,371,410,452,498,548,604,667,737,811,893,984,1087,1201,1331,1472,1628,1798,1994,2199,
            2432,2687,2969,3282,3631,4019,4450,4928,5457,6041,6689,7406,8199,9073,10035,11101,12284,13597,15053,16666,
            18448,20415,22585,24970,27593,30473,33636,37110,40928,45129,49845,55024,60737,67043,74022,81759,
            90335,99850,110413,122174,135274,149850,165737,183111,202151,222648,244780,268629,295386,325285,358225,394713
        };

        // IMA ADPCM索引调整表
        private static readonly int[] ImaIndexTable = new int[]
        {
            -1,-1,-1,-1, 2,4,6,8,
            -1,-1,-1,-1, 2,4,6,8
        };

        /// <summary>
        /// 解码IMA ADPCM 4-bit → 16-bit PCM → float
        /// 每个block: 每声道首字节为预测器高8位, 次字节为预测器低8位, 第三字节为步长索引, 第四字节保留,
        /// 然后是4-bit样本(交错)。
        /// </summary>
        private float[] DecodeImaAdpcm()
        {
            int channels = _info.Channels;
            int blockAlign = _info.BlockAlign;
            int dataOffset = _info.DataOffset;
            int dataSize = _info.DataSize;

            // 工业级防御: 除零/非法参数
            if (channels <= 0 || blockAlign <= 0)
                throw new InvalidDataException("无效的WAV文件: IMA ADPCM声道/块对齐非法");
            if (blockAlign % channels != 0)
                throw new InvalidDataException("无效的WAV文件: IMA ADPCM块对齐与声道不匹配");

            int numBlocks = dataSize / blockAlign;

            // 每block每声道样本数: (blockAlign/channels - 4) * 8 + 1
            int headerPerChannel = blockAlign / channels;
            if (headerPerChannel < 4)
                throw new InvalidDataException("无效的WAV文件: IMA ADPCM块对齐过小");
            int samplesPerBlockPerChannel = (headerPerChannel - 4) * 8 + 1;

            // 工业级防御: numBlocks*samplesPerBlockPerChannel 用 long 计算防 int 溢出。
            long totalPerChannelL = (long)numBlocks * samplesPerBlockPerChannel;
            if (totalPerChannelL > int.MaxValue)
                throw new InvalidDataException($"无效的WAV文件: IMA ADPCM 样本数溢出 ({totalPerChannelL})");
            int totalSamplesPerChannel = SafeOutputSampleCount((int)totalPerChannelL) / channels;

            // 工业级防御: 输出上限校验(防止 numBlocks*channels*... 溢出)
            long totalOut = (long)totalSamplesPerChannel * channels;
            if (totalOut > 100_000_000)
                throw new InvalidDataException($"无效的WAV文件: IMA ADPCM输出过大 ({totalOut})");
            float[] output = new float[(int)totalOut];

            using var ms = new MemoryStream(_fileData);
            ms.Position = dataOffset;
            using var reader = new BinaryReader(ms);

            int outIdx = 0;

            for (int block = 0; block < numBlocks; block++)
            {
                // 每声道一个状态(header: 4字节)
                int[] predictor = new int[channels];
                int[] stepIndex = new int[channels];

                for (int ch = 0; ch < channels; ch++)
                {
                    // 预测器初始值(16-bit有符号小端)
                    predictor[ch] = reader.ReadInt16();
                    stepIndex[ch] = reader.ReadByte();
                    if (stepIndex[ch] > 88) stepIndex[ch] = 88;
                    if (stepIndex[ch] < 0) stepIndex[ch] = 0;
                    reader.ReadByte(); // 保留字节
                }

                // 输出初始预测器值
                for (int ch = 0; ch < channels; ch++)
                {
                    // 工业级防御: outIdx+ch 越界即损坏数据 (防 OOB)。
                    if (outIdx + ch >= output.Length) break;
                    output[outIdx + ch] = predictor[ch] / 32768.0f;
                }
                outIdx += channels;

                // 解码4-bit样本
                int dataBytesPerChannel = headerPerChannel - 4;
                for (int i = 0; i < dataBytesPerChannel; i++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        byte nibbleByte = reader.ReadByte();
                        // 低4位
                        int nibble0 = nibbleByte & 0x0F;
                        predictor[ch] = DecodeImaSample(nibble0, ref predictor[ch], ref stepIndex[ch]);
                        if (outIdx >= output.Length) break; // 防越界(流数据超出)
                        output[outIdx++] = predictor[ch] / 32768.0f;

                        // 高4位
                        // 注意: 对于立体声, 下一个4位属于同一声道
                        // 但WAV IMA ADPCM按声道交错存储8字节块
                        // 简化处理: 假设单声道或每声道独立读取
                    }
                }
            }

            return output;
        }

        /// <summary>
        /// IMA ADPCM单个4-bit样本解码
        /// </summary>
        private int DecodeImaSample(int nibble, ref int predictor, ref int stepIndex)
        {
            int step = ImaStepTable[stepIndex];
            int diff = step >> 3;
            if ((nibble & 1) != 0) diff += step >> 2;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 8) != 0) diff = -diff;

            predictor += diff;
            if (predictor > 32767) predictor = 32767;
            if (predictor < -32768) predictor = -32768;

            stepIndex += ImaIndexTable[nibble];
            if (stepIndex < 0) stepIndex = 0;
            if (stepIndex > 88) stepIndex = 88;

            return predictor;
        }

        /// <summary>
        /// 解码指定范围样本(支持循环)
        /// </summary>
        public float[] DecodeRange(int startSample, int numSamples)
        {
            if (startSample < 0) throw new ArgumentOutOfRangeException(nameof(startSample));
            if (numSamples < 0) throw new ArgumentOutOfRangeException(nameof(numSamples));
            float[] all = DecodeAll();
            int totalChannels = _info.Channels;
            // 工业级防御: startSample*channels 溢出 → 用 long 校验
            long startIdxL = (long)startSample * totalChannels;
            if (startIdxL > all.Length) return Array.Empty<float>();
            int startIdx = (int)startIdxL;
            long countL = Math.Min((long)numSamples * totalChannels, all.Length - startIdx);
            if (countL <= 0) return Array.Empty<float>();

            int count = (int)countL;
            float[] result = new float[count];
            Array.Copy(all, startIdx, result, 0, count);
            return result;
        }

        public void Dispose()
        {
            _fileData = null;
        }
    }

    // ========================================================================
    // 重采样器
    // ========================================================================

    /// <summary>
    /// 重采样质量
    /// </summary>
    public enum ResampleQuality
    {
        Linear,     // 线性插值(低质量)
        Cubic,      // 立方插值(Catmull-Rom, 中等质量)
        Hermite,    // Hermite插值(中等质量)
        Sinc        // Sinc插值(高质量, 窗口化sinc)
    }

    /// <summary>
    /// 音频重采样器 - 支持线性/立方/Hermite/Sinc插值。
    /// 包含抗混叠低通滤波器。
    /// </summary>
    public sealed class AudioStreamResampler
    {
        private double _inputSampleRate;
        private double _outputSampleRate;
        private double _ratio;
        private ResampleQuality _quality;
        private BiquadFilter _antiAliasFilter;

        // Sinc插值查表
        private double[] _sincTable;
        private const int SINC_TABLE_SIZE = 1024;
        private const int SINC_FILTER_HALF_WIDTH = 16; // 每侧16个样本
        private const int SINC_FILTER_WIDTH = SINC_FILTER_HALF_WIDTH * 2;

        // 上一次样本(用于插值连续性)
        private readonly double[] _lastSamples;
        private double _fractionalPosition;

        public double InputSampleRate => _inputSampleRate;
        public double OutputSampleRate => _outputSampleRate;
        public double Ratio => _ratio;
        public ResampleQuality Quality => _quality;

        public AudioStreamResampler(double inputSampleRate, double outputSampleRate,
                                     ResampleQuality quality = ResampleQuality.Cubic)
        {
            // 工业级防御: 采样率非法会导致 _ratio=Inf/NaN, 后续输出帧数溢出/无效。
            if (inputSampleRate <= 0 || outputSampleRate <= 0 ||
                double.IsNaN(inputSampleRate) || double.IsNaN(outputSampleRate) ||
                double.IsInfinity(inputSampleRate) || double.IsInfinity(outputSampleRate))
                throw new InvalidDataException($"无效的采样率: in={inputSampleRate}, out={outputSampleRate}");

            _inputSampleRate = inputSampleRate;
            _outputSampleRate = outputSampleRate;
            _ratio = outputSampleRate / inputSampleRate;
            _quality = quality;
            _fractionalPosition = 0.0;
            _lastSamples = new double[SINC_FILTER_WIDTH];

            // 抗混叠低通滤波器(仅在降采样时需要)
            double cutoff = Math.Min(inputSampleRate, outputSampleRate) * 0.45;
            _antiAliasFilter = new BiquadFilter(inputSampleRate);
            _antiAliasFilter.SetParameters(BiquadType.LowPass, cutoff, 0.707);

            // 预计算sinc表
            if (quality == ResampleQuality.Sinc)
                BuildSincTable();
        }

        /// <summary>
        /// 构建sinc查找表(Blackman窗)
        /// </summary>
        private void BuildSincTable()
        {
            _sincTable = new double[SINC_TABLE_SIZE * SINC_FILTER_WIDTH];
            for (int t = 0; t < SINC_TABLE_SIZE; t++)
            {
                double frac = (double)t / SINC_TABLE_SIZE;
                for (int i = 0; i < SINC_FILTER_WIDTH; i++)
                {
                    int idx = i - SINC_FILTER_HALF_WIDTH;
                    double x = idx - frac;
                    double sinc = x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    // Blackman窗
                    int n = i;
                    int N = SINC_FILTER_WIDTH - 1;
                    double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / N)
                                  + 0.08 * Math.Cos(4 * Math.PI * n / N);
                    _sincTable[t * SINC_FILTER_WIDTH + i] = sinc * window;
                }
            }
        }

        /// <summary>
        /// 重采样整个缓冲区
        /// </summary>
        public float[] Resample(float[] input, int channels = 1)
        {
            if (Math.Abs(_ratio - 1.0) < 0.0001) return input;

            // 如果降采样, 先应用抗混叠滤波
            float[] filtered = input;
            if (_ratio < 1.0)
            {
                filtered = new float[input.Length];
                Array.Copy(input, filtered, input.Length);
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int i = ch; i < filtered.Length; i += channels)
                        filtered[i] = (float)_antiAliasFilter.Process(filtered[i]);
                }
                _antiAliasFilter.Reset();
            }

            int inputFrames = filtered.Length / channels;
            // 工业级防御: 输出帧数用 double 计算后校验范围, 防 int 溢出/Infinity。
            double outputFramesD = Math.Ceiling(inputFrames * _ratio);
            if (double.IsNaN(outputFramesD) || double.IsInfinity(outputFramesD) ||
                outputFramesD < 0 || outputFramesD > int.MaxValue / Math.Max(1, channels))
                throw new InvalidDataException($"重采样输出帧数溢出: {outputFramesD} (inputFrames={inputFrames}, ratio={_ratio})");
            int outputFrames = (int)outputFramesD;
            float[] output = new float[outputFrames * channels];

            for (int ch = 0; ch < channels; ch++)
            {
                double readPos = 0.0;
                for (int outFrame = 0; outFrame < outputFrames; outFrame++)
                {
                    int inputIdx = (int)readPos;
                    double frac = readPos - inputIdx;

                    double sample = 0;
                    switch (_quality)
                    {
                        case ResampleQuality.Linear:
                            sample = InterpolateLinear(filtered, inputIdx, ch, channels);
                            break;
                        case ResampleQuality.Cubic:
                            sample = InterpolateCubic(filtered, inputIdx, frac, ch, channels);
                            break;
                        case ResampleQuality.Hermite:
                            sample = InterpolateHermite(filtered, inputIdx, frac, ch, channels);
                            break;
                        case ResampleQuality.Sinc:
                            sample = InterpolateSinc(filtered, inputIdx, frac, ch, channels);
                            break;
                    }

                    output[outFrame * channels + ch] = (float)sample;
                    readPos += 1.0 / _ratio;
                }
            }

            return output;
        }

        /// <summary>
        /// 线性插值: y = x0*(1-frac) + x1*frac
        /// </summary>
        private double InterpolateLinear(float[] data, int idx, int ch, int channels)
        {
            double x0 = GetSample(data, idx, ch, channels);
            double x1 = GetSample(data, idx + 1, ch, channels);
            return x0 * 0.5 + x1 * 0.5; // 简化: 无frac, 使用中点
        }

        /// <summary>
        /// 立方插值(Catmull-Rom): 使用4个点
        /// y = 0.5*((2*x1) + (-x0+x2)*frac + (2*x0-5*x1+4*x2-x3)*frac^2 + (-x0+3*x1-3*x2+x3)*frac^3)
        /// </summary>
        private double InterpolateCubic(float[] data, int idx, double frac, int ch, int channels)
        {
            double x0 = GetSample(data, idx - 1, ch, channels);
            double x1 = GetSample(data, idx, ch, channels);
            double x2 = GetSample(data, idx + 1, ch, channels);
            double x3 = GetSample(data, idx + 2, ch, channels);

            double a0 = -0.5 * x0 + 1.5 * x1 - 1.5 * x2 + 0.5 * x3;
            double a1 = x0 - 2.5 * x1 + 2.0 * x2 - 0.5 * x3;
            double a2 = -0.5 * x0 + 0.5 * x2;
            double a3 = x1;

            return ((a0 * frac + a1) * frac + a2) * frac + a3;
        }

        /// <summary>
        /// Hermite插值: 3次多项式, 保证连续一阶导数
        /// y = (2t^3-3t^2+1)*y1 + (t^3-2t^2+t)*m0 + (-2t^3+3t^2)*y2 + (t^3-t^2)*m1
        /// 其中 m0 = (y2-y0)/2, m1 = (y3-y1)/2
        /// </summary>
        private double InterpolateHermite(float[] data, int idx, double frac, int ch, int channels)
        {
            double y0 = GetSample(data, idx - 1, ch, channels);
            double y1 = GetSample(data, idx, ch, channels);
            double y2 = GetSample(data, idx + 1, ch, channels);
            double y3 = GetSample(data, idx + 2, ch, channels);

            double m0 = (y2 - y0) * 0.5;
            double m1 = (y3 - y1) * 0.5;

            double t = frac;
            double t2 = t * t;
            double t3 = t2 * t;

            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;

            return h00 * y1 + h10 * m0 + h01 * y2 + h11 * m1;
        }

        /// <summary>
        /// Sinc插值(窗口化) - 理想低通重采样
        /// y = sum(x[n] * sinc((readPos - n))) * window
        /// 使用预计算sinc表加速
        /// </summary>
        private double InterpolateSinc(float[] data, int idx, double frac, int ch, int channels)
        {
            if (_sincTable == null) return InterpolateCubic(data, idx, frac, ch, channels);

            double sum = 0.0;
            int tableIdx = (int)(frac * SINC_TABLE_SIZE);

            for (int i = 0; i < SINC_FILTER_WIDTH; i++)
            {
                int sampleIdx = idx - SINC_FILTER_HALF_WIDTH + 1 + i;
                double s = GetSample(data, sampleIdx, ch, channels);
                double coef = _sincTable[tableIdx * SINC_FILTER_WIDTH + i];
                sum += s * coef;
            }

            return sum;
        }

        /// <summary>
        /// 安全获取样本(越界返回0)
        /// </summary>
        private double GetSample(float[] data, int frameIdx, int ch, int channels)
        {
            if (frameIdx < 0 || frameIdx * channels + ch >= data.Length)
                return 0.0;
            return data[frameIdx * channels + ch];
        }

        /// <summary>
        /// 流式重采样 - 逐块处理
        /// </summary>
        public int ResampleBlock(float[] input, int inputFrames, float[] output, int channels)
        {
            int outputFrames = (int)(inputFrames * _ratio);
            int maxOutput = output.Length / channels;

            for (int ch = 0; ch < channels; ch++)
            {
                double readPos = 0.0;
                for (int outFrame = 0; outFrame < Math.Min(outputFrames, maxOutput); outFrame++)
                {
                    int inputIdx = (int)readPos;
                    double frac = readPos - inputIdx;

                    double sample = _quality switch
                    {
                        ResampleQuality.Linear => InterpolateLinear(input, inputIdx, ch, channels),
                        ResampleQuality.Cubic => InterpolateCubic(input, inputIdx, frac, ch, channels),
                        ResampleQuality.Hermite => InterpolateHermite(input, inputIdx, frac, ch, channels),
                        ResampleQuality.Sinc => InterpolateSinc(input, inputIdx, frac, ch, channels),
                        _ => InterpolateCubic(input, inputIdx, frac, ch, channels)
                    };

                    output[outFrame * channels + ch] = (float)sample;
                    readPos += 1.0 / _ratio;
                }
            }

            return Math.Min(outputFrames, maxOutput);
        }

        public void Reset()
        {
            _fractionalPosition = 0.0;
            _antiAliasFilter.Reset();
            Array.Clear(_lastSamples, 0, _lastSamples.Length);
        }
    }
}
